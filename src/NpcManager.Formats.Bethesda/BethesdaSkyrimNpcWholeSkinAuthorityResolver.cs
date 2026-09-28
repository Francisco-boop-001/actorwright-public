using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Resolves the winning female race-skin record graph and its exact copied
/// texture providers. This service is read-only and grants no runtime or
/// rendered-neck authority.
/// </summary>
public sealed class BethesdaSkyrimNpcWholeSkinAuthorityResolver(
    ISkyrimFaceRecordPluginAuthorityLoader authorityLoader,
    ISkyrimAssetAuthorityPlanner assetAuthorityPlanner)
    : ISkyrimNpcWholeSkinAuthorityResolver
{
    private static readonly RecordSignature RaceSignature = new("RACE");
    private static readonly RecordSignature ArmorSignature = new("ARMO");
    private static readonly RecordSignature ArmorAddonSignature = new("ARMA");
    private static readonly RecordSignature OutfitSignature = new("OTFT");
    private static readonly RecordSignature TextureSetSignature = new("TXST");
    private const int MaximumPlugins = 64;
    private const int MaximumMeshEmbeddedTextureBytes = 128 * 1024 * 1024;

    public async ValueTask<SkyrimNpcWholeSkinAuthorityResult> ResolveAsync(
        SkyrimNpcWholeSkinAuthorityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimFaceRecordPluginAuthorityResult reopened =
            await authorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    request.Edition,
                    request.DataRoot,
                    request.PluginOrder.Select(item => item.Plugin)
                        .ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(reopened.Diagnostics);
        if (!reopened.Accepted ||
            !MatchesReviewedAuthorities(reopened.Authorities, request.PluginOrder))
        {
            if (reopened.Accepted)
            {
                diagnostics.Add(Error("skyrim-whole-skin-plugin-order-stale",
                    "The copied plugin order no longer matches the reviewed paths and SHA-256 values."));
            }
            return Refused(diagnostics);
        }

        var races = new Dictionary<FormKey, RaceSnapshot>();
        var armors = new Dictionary<FormKey, ArmorSnapshot>();
        var addons = new Dictionary<FormKey, ArmorAddonSnapshot>();
        var outfits = new Dictionary<FormKey, OutfitSnapshot>();
        var textures = new Dictionary<FormKey, TextureSetSnapshot>();
        try
        {
            foreach (SkyrimFaceRecordPluginAuthority plugin in reopened.Authorities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = new SkyrimFaceRecordProvider(
                    plugin.Plugin, plugin.Path, plugin.ExpectedSha256);
                using var mod = SkyrimMod.CreateFromBinaryOverlay(
                    plugin.Path.Value, SkyrimRelease.SkyrimSE);
                foreach (IRaceGetter raceRecord in mod.Races)
                {
                    races[raceRecord.FormKey] = new RaceSnapshot(
                        provider,
                        raceRecord.FormKey,
                        raceRecord.Skin.FormKeyNullable,
                        raceRecord.ArmorRace.FormKeyNullable,
                        raceRecord.IsDeleted);
                }
                foreach (IArmorGetter armor in mod.Armors)
                {
                    armors[armor.FormKey] = new ArmorSnapshot(
                        provider,
                        armor.FormKey,
                        armor.Race.FormKeyNullable,
                        armor.Armature.Select(item => item.FormKey)
                            .ToImmutableArray(),
                        armor.IsDeleted);
                }
                foreach (IArmorAddonGetter addon in mod.ArmorAddons)
                {
                    addons[addon.FormKey] = new ArmorAddonSnapshot(
                        provider,
                        addon.FormKey,
                        addon.BodyTemplate is null
                            ? 0U
                            : (uint)addon.BodyTemplate.FirstPersonFlags,
                        addon.Race.FormKeyNullable,
                        addon.AdditionalRaces.Select(item => item.FormKey)
                            .ToImmutableArray(),
                        addon.SkinTexture?.Female?.FormKey,
                        addon.WorldModel?.Female?.File?.ToString(),
                        addon.IsDeleted);
                }
                foreach (IOutfitGetter outfit in mod.Outfits)
                {
                    outfits[outfit.FormKey] = new OutfitSnapshot(
                        provider,
                        outfit.FormKey,
                        (outfit.Items ?? [])
                            .Select(item => item.FormKey)
                            .ToImmutableArray(),
                        outfit.IsDeleted);
                }
                foreach (ITextureSetGetter texture in mod.TextureSets)
                {
                    textures[texture.FormKey] = new TextureSetSnapshot(
                        provider,
                        texture.FormKey,
                        texture.Diffuse,
                        texture.NormalOrGloss,
                        texture.GlowOrDetailMap,
                        texture.BacklightMaskOrSpecular,
                        texture.IsDeleted);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("skyrim-whole-skin-plugin-malformed",
                $"The reviewed plugin order could not be decoded as a whole-skin graph: {exception.Message}"));
            return Refused(diagnostics);
        }

        FormKey raceKey = Key(request.Race);
        if (!races.TryGetValue(raceKey, out RaceSnapshot? race) ||
            race.IsDeleted || race.Skin is null || race.Skin.Value.IsNull)
        {
            diagnostics.Add(Error("skyrim-whole-skin-race-wnam",
                $"Winning race {request.Race} has no admitted WNAM skin armor."));
            return Refused(diagnostics);
        }
        if (!armors.TryGetValue(race.Skin.Value, out ArmorSnapshot? skin) ||
            skin.IsDeleted)
        {
            diagnostics.Add(Error("skyrim-whole-skin-armor",
                "The race WNAM skin armor is missing or deleted in the reviewed order."));
            return Refused(diagnostics);
        }
        ImmutableHashSet<FormKey> compatibleRaces =
            race.ArmorRace is { } armorRaceKey && !armorRaceKey.IsNull
                ? ImmutableHashSet.Create(raceKey, armorRaceKey)
                : ImmutableHashSet.Create(raceKey);
        if (skin.Race is { } armorRace && !armorRace.IsNull &&
            !compatibleRaces.Contains(armorRace))
        {
            diagnostics.Add(new Diagnostic(
                "skyrim-whole-skin-armor-default-race",
                DiagnosticSeverity.Info,
                "The race WNAM armor declares a different default race; exact ARMA primary/additional-race compatibility remains authoritative for each skin region."));
        }
        if (skin.Armature.IsDefaultOrEmpty ||
            skin.Armature.Distinct().Count() != skin.Armature.Length)
        {
            diagnostics.Add(Error("skyrim-whole-skin-armature",
                "The race WNAM armor requires a non-empty, duplicate-free ARMA list."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<ArmorAddonSnapshot> skinAddons = skin.Armature
            .Select(key => addons.TryGetValue(key, out ArmorAddonSnapshot? addon)
                ? addon
                : null)
            .Where(item => item is not null)
            .Cast<ArmorAddonSnapshot>()
            .ToImmutableArray();
        if (skinAddons.Length != skin.Armature.Length ||
            skinAddons.Any(item => item.IsDeleted))
        {
            diagnostics.Add(Error("skyrim-whole-skin-addon",
                "One or more WNAM ARMA records are missing or deleted."));
            return Refused(diagnostics);
        }

        var regions = ImmutableArray.CreateBuilder<SkyrimNpcSkinRegionAuthority>(3);
        foreach ((SkyrimNpcSkinRegion region, uint bit) in RegionBits())
        {
            ArmorAddonSnapshot[] matches = skinAddons
                .Where(item =>
                    (item.SlotMask & bit) != 0 &&
                    IsRaceCompatible(item, compatibleRaces))
                .ToArray();
            if (matches.Length != 1)
            {
                diagnostics.Add(Error("skyrim-whole-skin-region-coverage",
                    $"Whole-skin authority requires exactly one female {region} ARMA route; found {matches.Length}."));
                continue;
            }
            ArmorAddonSnapshot addon = matches[0];
            if (addon.FemaleTextureSet is not { } textureKey ||
                textureKey.IsNull ||
                !textures.TryGetValue(textureKey, out TextureSetSnapshot? texture) ||
                texture.IsDeleted)
            {
                if (!request.AllowMeshEmbeddedSkinTextureRoute)
                {
                    diagnostics.Add(Error("skyrim-whole-skin-texture-set",
                        $"The {region} ARMA has no winning female TXST."));
                    continue;
                }

                SkyrimNpcSkinRegionAuthority? meshEmbedded =
                    await ResolveMeshEmbeddedTextureRegionAsync(
                        request, region, addon, diagnostics, cancellationToken)
                        .ConfigureAwait(false);
                if (meshEmbedded is not null) regions.Add(meshEmbedded);
                continue;
            }

            SkyrimNpcSkinTexturePaths paths;
            try
            {
                paths = new SkyrimNpcSkinTexturePaths(
                    RequiredTexture(texture.Diffuse, region, "diffuse"),
                    RequiredTexture(texture.Normal, region, "normal"),
                    RequiredTexture(texture.Subsurface, region, "subsurface"),
                    RequiredTexture(texture.Specular, region, "specular"));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error("skyrim-whole-skin-texture-path",
                    exception.Message));
                continue;
            }

            ImmutableArray<AssetPath> required =
            [
                paths.Diffuse,
                paths.Normal,
                paths.Subsurface,
                paths.Specular
            ];
            SkyrimAssetAuthorityPlanResult assetResult =
                await assetAuthorityPlanner.PlanAsync(
                    new SkyrimAssetAuthorityPlanRequest(
                        request.Edition, request.DataRoot, required),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(assetResult.Diagnostics);
            if (!assetResult.Accepted ||
                assetResult.Authorities.Length != required.Length)
                continue;

            regions.Add(new SkyrimNpcSkinRegionAuthority(
                region,
                addon.SlotMask,
                Binding(ArmorAddonSignature, addon.FormKey, addon.Provider),
                Binding(TextureSetSignature, texture.FormKey, texture.Provider),
                paths,
                assetResult.Authorities));
        }
        if (HasErrors(diagnostics) || regions.Count != 3)
            return Refused(diagnostics);

        SkyrimNpcExposedOutfitSkinBinding? exposedOutfitSkinBinding = null;
        if (request.DefaultOutfit is { } selectedOutfit)
        {
            FormKey outfitKey = Key(selectedOutfit);
            if (!outfits.TryGetValue(outfitKey, out OutfitSnapshot? outfit) ||
                outfit.IsDeleted)
            {
                diagnostics.Add(Error("skyrim-whole-skin-outfit",
                    $"Selected default outfit {selectedOutfit} is missing or deleted."));
                return Refused(diagnostics);
            }

            var candidates = new List<(ArmorSnapshot Armor, ArmorAddonSnapshot Addon)>();
            foreach (FormKey itemKey in outfit.Items)
            {
                if (!armors.TryGetValue(itemKey, out ArmorSnapshot? armor) ||
                    armor.IsDeleted)
                    continue;
                foreach (FormKey addonKey in armor.Armature)
                {
                    if (!addons.TryGetValue(addonKey, out ArmorAddonSnapshot? addon) ||
                        addon.IsDeleted)
                    {
                        diagnostics.Add(Error("skyrim-whole-skin-outfit-addon",
                            $"Outfit armor {Reference(armor.FormKey)} has a missing or deleted ARMA."));
                        continue;
                    }
                    if ((addon.SlotMask & 0x04U) != 0 &&
                        IsRaceCompatible(addon, compatibleRaces))
                        candidates.Add((armor, addon));
                }
            }
            if (HasErrors(diagnostics)) return Refused(diagnostics);
            if (candidates.Count > 1)
            {
                diagnostics.Add(Error("skyrim-whole-skin-outfit-body-ambiguous",
                    $"Selected outfit exposes {candidates.Count} race-compatible female torso routes; exactly zero or one is supported."));
                return Refused(diagnostics);
            }
            if (candidates.Count == 1)
            {
                (ArmorSnapshot armor, ArmorAddonSnapshot addon) = candidates[0];
                RaceMenuNpcFormBinding? targetBodyTextureSet = regions
                    .Single(item => item.Region == SkyrimNpcSkinRegion.Body)
                    .TextureSet;
                if (targetBodyTextureSet is null)
                {
                    diagnostics.Add(Error(
                        "skyrim-whole-skin-outfit-body-mesh-embedded",
                        "Selected outfit exposed-body repair currently requires an accepted body TXST; mesh-embedded naked skin authority is supported only for naked visual NPCs."));
                    return Refused(diagnostics);
                }
                FormKey targetTextureKey = Key(targetBodyTextureSet.Reference);
                if (addon.FemaleTextureSet != targetTextureKey)
                {
                    exposedOutfitSkinBinding =
                        new SkyrimNpcExposedOutfitSkinBinding(
                            Binding(OutfitSignature, outfit.FormKey, outfit.Provider),
                            Binding(ArmorSignature, armor.FormKey, armor.Provider),
                            Binding(ArmorAddonSignature, addon.FormKey, addon.Provider),
                            targetBodyTextureSet,
                            addon.SlotMask & 0x04U);
                    diagnostics.Add(new Diagnostic(
                        "skyrim-whole-skin-outfit-private-binding",
                        DiagnosticSeverity.Info,
                        "The selected outfit exposes a female torso outside the accepted body TXST; an output-owned ARMA/ARMO/OTFT binding is required."));
                }
            }
        }

        var authority = new SkyrimNpcWholeSkinAuthority(
            SkyrimNpcSkinRouteKind.InheritedRace,
            request.Race,
            Binding(RaceSignature, race.FormKey, race.Provider),
            Binding(ArmorSignature, skin.FormKey, skin.Provider),
            regions.ToImmutable(),
            RuntimeAuthority: false)
        {
            ExposedOutfitSkinBinding = exposedOutfitSkinBinding
        };
        diagnostics.Add(new Diagnostic(
            "skyrim-whole-skin-static-authority",
            DiagnosticSeverity.Info,
            "Resolved one complete hash-bound inherited race skin route; Skyrim runtime selection and rendered neck continuity remain unproven."));
        return new SkyrimNpcWholeSkinAuthorityResult(
            true, authority, diagnostics.ToImmutable());
    }

    private async ValueTask<SkyrimNpcSkinRegionAuthority?>
        ResolveMeshEmbeddedTextureRegionAsync(
            SkyrimNpcWholeSkinAuthorityRequest request,
            SkyrimNpcSkinRegion region,
            ArmorAddonSnapshot addon,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        WorkspacePath meshPath;
        string meshLabel;
        if (request.BodyMeshAuthority is { } bodyMeshAuthority)
        {
            RaceMenuNpcBodyMeshRole role = BodyMeshRoleForRegion(region);
            RaceMenuNpcBodyMeshAsset? mesh = bodyMeshAuthority.Meshes
                .FirstOrDefault(item => item.Role == role);
            if (mesh is null)
            {
                diagnostics.Add(Error(
                    "skyrim-whole-skin-mesh-embedded-body-authority",
                    $"The schema-7 body mesh authority does not contain {role.ToWireName()} for the {region} no-TXST ARMA."));
                return null;
            }

            Sha256Hash actualHash;
            try
            {
                actualHash = await HashFileAsync(
                    mesh.Source, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               ArgumentException or
                                               NotSupportedException)
            {
                diagnostics.Add(Error(
                    "skyrim-whole-skin-mesh-embedded-body-hash",
                    exception.Message));
                return null;
            }
            if (actualHash != mesh.ExpectedSha256)
            {
                diagnostics.Add(Error(
                    "skyrim-whole-skin-mesh-embedded-body-hash",
                    $"The schema-7 body mesh {role.ToWireName()} hash {actualHash} does not match {mesh.ExpectedSha256}."));
                return null;
            }

            meshPath = mesh.Source;
            meshLabel = mesh.Destination.Value;
        }
        else
        {
            AssetPath sourceModel;
            try
            {
                sourceModel = RequiredMesh(addon.FemaleModel, region);
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error(
                    "skyrim-whole-skin-mesh-embedded-model",
                    exception.Message));
                return null;
            }

            SkyrimAssetAuthorityPlanResult meshResult =
                await assetAuthorityPlanner.PlanAsync(
                    new SkyrimAssetAuthorityPlanRequest(
                        request.Edition, request.DataRoot, [sourceModel]),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(meshResult.Diagnostics);
            if (!meshResult.Accepted || meshResult.Authorities.Length != 1)
                return null;

            SkyrimAssetAuthority meshAuthority = meshResult.Authorities[0];
            if (meshAuthority.ProviderKind != AssetProviderKind.Loose)
            {
                diagnostics.Add(Error(
                    "skyrim-whole-skin-mesh-embedded-archive",
                    $"The {region} ARMA has no female TXST and its source mesh is archive-provided; mesh-embedded texture authority currently requires a loose copied NIF."));
                return null;
            }
            meshPath = meshAuthority.ProviderPath;
            meshLabel = sourceModel.Value;
        }

        ImmutableArray<AssetPath> textures;
        try
        {
            textures = ReadEmbeddedTexturePaths(meshPath);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           RegexMatchTimeoutException)
        {
            diagnostics.Add(Error(
                "skyrim-whole-skin-mesh-embedded-textures",
                exception.Message));
            return null;
        }
        if (textures.Length is <= 0 or > 16)
        {
            diagnostics.Add(Error(
                "skyrim-whole-skin-mesh-embedded-texture-count",
                $"The {region} source mesh must expose 1-16 embedded DDS texture routes; found {textures.Length}."));
            return null;
        }

        SkyrimAssetAuthorityPlanResult textureResult =
            await assetAuthorityPlanner.PlanAsync(
                new SkyrimAssetAuthorityPlanRequest(
                    request.Edition, request.DataRoot, textures),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(textureResult.Diagnostics);
        if (!textureResult.Accepted ||
            textureResult.Authorities.Length != textures.Length)
            return null;

        diagnostics.Add(new Diagnostic(
            "skyrim-whole-skin-mesh-embedded-textures",
            DiagnosticSeverity.Warning,
            $"The {region} ARMA has no female TXST; admitted schema-7 mesh-embedded texture authority from '{meshLabel}' with {textures.Length} DDS route(s)."));
        return new SkyrimNpcSkinRegionAuthority(
            region,
            addon.SlotMask,
            Binding(ArmorAddonSignature, addon.FormKey, addon.Provider),
            null,
            null,
            textureResult.Authorities);
    }

    private static RaceMenuNpcBodyMeshRole BodyMeshRoleForRegion(
        SkyrimNpcSkinRegion region) =>
        region switch
        {
            SkyrimNpcSkinRegion.Body => RaceMenuNpcBodyMeshRole.Body1,
            SkyrimNpcSkinRegion.Hands => RaceMenuNpcBodyMeshRole.Hands1,
            SkyrimNpcSkinRegion.Feet => RaceMenuNpcBodyMeshRole.Feet1,
            _ => throw new InvalidDataException("Unsupported skin region.")
        };

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (!info.Exists || info.Length is <= 0 or > MaximumMeshEmbeddedTextureBytes ||
            info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "Mesh-embedded body authority requires one bounded ordinary K-local NIF.");
        }
        await using var stream = new FileStream(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)));
    }

    private static RaceMenuNpcFormBinding Binding(
        RecordSignature signature,
        FormKey key,
        SkyrimFaceRecordProvider provider)
    {
        FormReference reference = Reference(key);
        return new RaceMenuNpcFormBinding(
            signature,
            reference,
            reference,
            provider.Plugin,
            provider.Path,
            provider.Sha256,
            null);
    }

    private static bool IsRaceCompatible(
        ArmorAddonSnapshot addon,
        ImmutableHashSet<FormKey> races) =>
        addon.Race is { } primary && races.Contains(primary) ||
        addon.AdditionalRaces.Any(races.Contains);

    private static ImmutableArray<(SkyrimNpcSkinRegion Region, uint Bit)>
        RegionBits() =>
    [
        (SkyrimNpcSkinRegion.Body, 0x04U),
        (SkyrimNpcSkinRegion.Hands, 0x08U),
        (SkyrimNpcSkinRegion.Feet, 0x80U)
    ];

    private static AssetPath RequiredTexture(
        string? value,
        SkyrimNpcSkinRegion region,
        string channel)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                $"The {region} TXST has no required {channel} texture.");
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            normalized = "textures/" + normalized;
        if (!normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"The {region} {channel} route is not a DDS texture.");
        return new AssetPath(normalized);
    }

    private static AssetPath RequiredMesh(
        string? value,
        SkyrimNpcSkinRegion region)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                $"The {region} ARMA has no required female source mesh for mesh-embedded texture authority.");
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
            normalized = "meshes/" + normalized;
        if (!normalized.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"The {region} female mesh route is not a NIF mesh.");
        return new AssetPath(normalized);
    }

    private static ImmutableArray<AssetPath> ReadEmbeddedTexturePaths(
        WorkspacePath mesh)
    {
        var info = new FileInfo(mesh.Value);
        if (!info.Exists || info.Length is <= 0 or > MaximumMeshEmbeddedTextureBytes ||
            info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "Mesh-embedded texture authority requires one bounded ordinary copied NIF.");
        }
        byte[] bytes = File.ReadAllBytes(mesh.Value);
        string text = System.Text.Encoding.Latin1.GetString(bytes);
        return Regex.Matches(
                text,
                @"[A-Za-z0-9_! './\\()\[\]-]{3,}\.dds",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2))
            .Select(match => RequiredTexture(
                match.Value, SkyrimNpcSkinRegion.Body, "mesh-embedded"))
            .DistinctBy(path => path.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static void ValidateRequest(
        SkyrimNpcWholeSkinAuthorityRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("skyrim-whole-skin-edition",
                "Whole-skin authority supports Skyrim Special Edition only."));
        if (request.Sex != NpcSex.Female)
            diagnostics.Add(Error("skyrim-whole-skin-sex",
                "The first whole-skin authority implementation supports female NPCs only."));
        if (string.IsNullOrWhiteSpace(request.DataRoot.Value) ||
            !Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(Error("skyrim-whole-skin-data-root",
                "A reviewed copied Data root is required."));
        if (string.IsNullOrWhiteSpace(request.Race.Plugin.Value) ||
            request.Race.FormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("skyrim-whole-skin-race",
                "A nonzero plugin-local race reference is required."));
        if (request.DefaultOutfit is { } outfit &&
            (string.IsNullOrWhiteSpace(outfit.Plugin.Value) ||
             outfit.FormId.Value is 0 or > 0x00FF_FFFF))
            diagnostics.Add(Error("skyrim-whole-skin-outfit-reference",
                "DefaultOutfit must be a nonzero plugin-local OTFT reference."));
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Length > MaximumPlugins ||
            request.PluginOrder.Any(item => item is null))
        {
            diagnostics.Add(Error("skyrim-whole-skin-plugin-order",
                $"PluginOrder must contain 1-{MaximumPlugins} reviewed authorities."));
            return;
        }
        if (request.PluginOrder.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.PluginOrder.Length ||
            request.PluginOrder.Select(item => item.Path.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.PluginOrder.Length)
        {
            diagnostics.Add(Error("skyrim-whole-skin-plugin-order-duplicate",
                "PluginOrder may not repeat a plugin identity or provider path."));
        }
    }

    private static bool MatchesReviewedAuthorities(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> current,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> reviewed)
    {
        if (current.Length != reviewed.Length) return false;
        for (var index = 0; index < current.Length; index++)
        {
            if (!string.Equals(current[index].Plugin.Value,
                    reviewed[index].Plugin.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current[index].Path.Value,
                    reviewed[index].Path.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                current[index].ExpectedSha256 != reviewed[index].ExpectedSha256)
                return false;
        }
        return true;
    }

    private static FormKey Key(FormReference reference) => new(
        ModKey.FromNameAndExtension(reference.Plugin.Value),
        reference.FormId.Value);

    private static FormReference Reference(FormKey key) => new(
        new PluginName(key.ModKey.ToString()),
        new FormId(key.ID));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimNpcWholeSkinAuthorityResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record RaceSnapshot(
        SkyrimFaceRecordProvider Provider,
        FormKey FormKey,
        FormKey? Skin,
        FormKey? ArmorRace,
        bool IsDeleted);

    private sealed record ArmorSnapshot(
        SkyrimFaceRecordProvider Provider,
        FormKey FormKey,
        FormKey? Race,
        ImmutableArray<FormKey> Armature,
        bool IsDeleted);

    private sealed record ArmorAddonSnapshot(
        SkyrimFaceRecordProvider Provider,
        FormKey FormKey,
        uint SlotMask,
        FormKey? Race,
        ImmutableArray<FormKey> AdditionalRaces,
        FormKey? FemaleTextureSet,
        string? FemaleModel,
        bool IsDeleted);

    private sealed record OutfitSnapshot(
        SkyrimFaceRecordProvider Provider,
        FormKey FormKey,
        ImmutableArray<FormKey> Items,
        bool IsDeleted);

    private sealed record TextureSetSnapshot(
        SkyrimFaceRecordProvider Provider,
        FormKey FormKey,
        string? Diffuse,
        string? Normal,
        string? Subsurface,
        string? Specular,
        bool IsDeleted);
}
