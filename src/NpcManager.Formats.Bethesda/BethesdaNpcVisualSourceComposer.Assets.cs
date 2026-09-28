using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed partial class BethesdaNpcVisualSourceComposer
{
    private static async ValueTask AddArmorMeshesAsync(
        FormKey armorKey,
        ImmutableHashSet<FormKey> compatibleRaceKeys,
        NpcSex sex,
        NpcVisualAssetRole requestedRole,
        RecordGraph graph,
        PreviewAssetResolver resolver,
        Dictionary<string, NpcVisualAsset> materialized,
        ImmutableArray<NpcVisualAsset>.Builder assets,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!graph.Armors.TryGetValue(
                armorKey, out ProviderRecord<Armor>? armor) ||
            armor.Record.IsDeleted)
        {
            diagnostics.Add(Error(
                "npc-preview-armor-missing",
                $"ARMO {armorKey} is unresolved."));
            return;
        }
        foreach (IFormLinkGetter<IArmorAddonGetter> link in
                 armor.Record.Armature)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!graph.ArmorAddons.TryGetValue(
                    link.FormKey,
                    out ProviderRecord<ArmorAddon>? addonRecord) ||
                addonRecord.Record.IsDeleted)
            {
                diagnostics.Add(Error(
                    "npc-preview-armor-addon-missing",
                    $"ARMA {link.FormKey} is unresolved."));
                continue;
            }
            ArmorAddon addon = addonRecord.Record;
            bool compatible =
                addon.Race.FormKey.IsNull ||
                compatibleRaceKeys.Contains(
                    addon.Race.FormKey) ||
                addon.AdditionalRaces.Any(item =>
                    compatibleRaceKeys.Contains(
                        item.FormKey));
            if (!compatible) continue;
            string? model = sex == NpcSex.Female
                ? addon.WorldModel?.Female?.File
                : addon.WorldModel?.Male?.File;
            if (string.IsNullOrWhiteSpace(model))
                continue;
            string modelPath = NormalizeMeshPath(model);
            NpcVisualAssetRole role =
                requestedRole == NpcVisualAssetRole.Outfit
                    ? requestedRole
                    : ClassifyBodyRole(modelPath);
            NpcVisualAsset? asset =
                await MaterializeNifAsync(
                    resolver,
                    modelPath,
                    role,
                    materialized,
                    diagnostics,
                    required: true,
                    cancellationToken);
            if (asset is null) continue;
            asset = asset with
            {
                Role = role,
                BipedSlotMask =
                    (uint)(addon.BodyTemplate?.FirstPersonFlags ??
                           (BipedObjectFlag)0)
            };

            FormKey? textureSet = sex == NpcSex.Female
                ? addon.SkinTexture?.Female?.FormKeyNullable
                : addon.SkinTexture?.Male?.FormKeyNullable;
            if (textureSet is { } textureKey &&
                graph.TextureSets.TryGetValue(
                    textureKey,
                    out ProviderRecord<TextureSet>? textureRecord) &&
                !textureRecord.Record.IsDeleted)
            {
                asset = await ApplyTextureSetOverrideAsync(
                    asset,
                    textureRecord.Record,
                    resolver,
                    materialized,
                    diagnostics,
                    cancellationToken);
            }
            asset = await AttachLowWeightCompanionAsync(
                asset,
                modelPath,
                resolver,
                materialized,
                diagnostics,
                cancellationToken);
            assets.Add(asset);
        }
    }

    private static async ValueTask<NpcVisualAsset>
        AttachLowWeightCompanionAsync(
            NpcVisualAsset source,
            string highWeightPath,
            PreviewAssetResolver resolver,
            Dictionary<string, NpcVisualAsset> materialized,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        if (!highWeightPath.EndsWith(
                "_1.nif",
                StringComparison.OrdinalIgnoreCase))
            return source;

        string lowWeightPath =
            highWeightPath[..^"_1.nif".Length] + "_0.nif";
        NpcVisualAsset? low = await MaterializeSimpleAsync(
            resolver,
            lowWeightPath,
            source.Role,
            materialized,
            diagnostics,
            required: false,
            cancellationToken);
        if (low is null)
        {
            diagnostics.Add(Warning(
                "npc-preview-weight-companion-missing",
                $"Mesh '{source.AssetPath}' has no resolved _0.nif companion; a non-100 actor weight cannot be reproduced faithfully."));
            return source;
        }
        return source with
        {
            LowWeightAssetPath = low.AssetPath,
            LowWeightSha256 = low.Sha256,
            LowWeightMaterializedPath =
                low.MaterializedPath
        };
    }

    private static async ValueTask<NpcVisualAsset>
        ApplyTextureSetOverrideAsync(
            NpcVisualAsset source,
            TextureSet textureSet,
            PreviewAssetResolver resolver,
            Dictionary<string, NpcVisualAsset> materialized,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        (int Slot, string? Value)[] values =
        [
            (0, textureSet.Diffuse),
            (1, textureSet.NormalOrGloss),
            (2, textureSet.GlowOrDetailMap),
            (3, textureSet.Height),
            (4, textureSet.Environment),
            (5, textureSet.EnvironmentMaskOrSubsurfaceTint),
            (6, textureSet.Multilayer),
            (7, textureSet.BacklightMaskOrSpecular)
        ];
        var slots =
            ImmutableArray.CreateBuilder<NpcVisualTextureSlot>();
        foreach ((int slot, string? value) in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            string path = NormalizeTexturePath(value);
            NpcVisualAsset? texture =
                await MaterializeSimpleAsync(
                    resolver,
                    path,
                    NpcVisualAssetRole.Texture,
                    materialized,
                    diagnostics,
                    required: false,
                    cancellationToken);
            if (texture is null) continue;
            slots.Add(new(
                slot,
                TextureSemantic(slot),
                texture.AssetPath,
                texture.Provider,
                texture.Sha256,
                texture.MaterializedPath));
        }
        if (slots.Count == 0) return source;
        ImmutableArray<NpcVisualTextureSlot> overrides =
            slots.ToImmutable();
        ImmutableArray<NpcVisualMaterial> materials =
            source.Materials.IsDefaultOrEmpty
                ? [
                    new NpcVisualMaterial(
                        $"TXST:{textureSet.FormKey}",
                        SkyrimNpcVisualMaterialRouting
                            .SkinTintShaderType,
                        overrides,
                        0,
                        0,
                        false,
                        1,
                        null)
                ]
                : SkyrimNpcVisualMaterialRouting
                    .ApplySkinTextureSet(
                        source.Materials,
                        overrides);
        if (!source.Materials.IsDefaultOrEmpty &&
            !source.Materials.Any(material =>
                material.ShaderType ==
                SkyrimNpcVisualMaterialRouting
                    .SkinTintShaderType))
            diagnostics.Add(Warning(
                "npc-preview-skin-texture-set-unqualified",
                $"NIF '{source.AssetPath}' has an armor-addon skin TXST, but no structurally proven SkinTint material; non-skin shapes were preserved unchanged."));
        return source with { Materials = materials };
    }

    private static async ValueTask<NpcVisualAsset?>
        MaterializeNifAsync(
            PreviewAssetResolver resolver,
            string requestedPath,
            NpcVisualAssetRole role,
            Dictionary<string, NpcVisualAsset> materialized,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            bool required,
            CancellationToken cancellationToken)
    {
        NpcVisualAsset? asset = await MaterializeSimpleAsync(
            resolver,
            NormalizeMeshPath(requestedPath),
            role,
            materialized,
            diagnostics,
            required,
            cancellationToken);
        if (asset is null) return null;
        asset = asset with { Role = role };

        byte[] bytes = await File.ReadAllBytesAsync(
            asset.MaterializedPath.Value,
            cancellationToken);
        ImmutableArray<NpcVisualMaterial> materials =
            await ResolveNifMaterialsAsync(
                asset.AssetPath.Value,
                bytes,
                resolver,
                materialized,
                diagnostics,
                cancellationToken);
        return asset with { Materials = materials };
    }

    private static async ValueTask<
        ImmutableArray<NpcVisualMaterial>>
        ResolveNifMaterialsAsync(
            string nifLabel,
            byte[] bytes,
            PreviewAssetResolver resolver,
            Dictionary<string, NpcVisualAsset> materialized,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        ImmutableArray<SseNifVisualMaterialDescriptor> descriptors;
        try
        {
            descriptors =
                SseNifVisualMaterialReader.Read(bytes);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Warning(
                "npc-preview-nif-material-structure",
                $"NIF '{nifLabel}' material routing could not be read structurally: {exception.Message}"));
            descriptors = FindTextureSets(bytes)
                .Select((textures, index) =>
                    new SseNifVisualMaterialDescriptor(
                        $"embedded-texture-set-{index}",
                        0,
                        0,
                        0,
                        false,
                        1,
                        null,
                        textures))
                .ToImmutableArray();
        }
        var materials =
            ImmutableArray.CreateBuilder<NpcVisualMaterial>();
        foreach (SseNifVisualMaterialDescriptor descriptor in
                 descriptors)
        {
            var slots =
                ImmutableArray.CreateBuilder<NpcVisualTextureSlot>();
            for (int slot = 0;
                 slot < descriptor.Textures.Length;
                 slot++)
            {
                if (string.IsNullOrWhiteSpace(
                        descriptor.Textures[slot]))
                    continue;
                string path;
                try
                {
                    path = NormalizeTexturePath(
                        descriptor.Textures[slot]);
                }
                catch (ArgumentException exception)
                {
                    diagnostics.Add(Warning(
                        "npc-preview-material-path-invalid",
                        exception.Message));
                    continue;
                }
                NpcVisualAsset? texture =
                    await MaterializeSimpleAsync(
                        resolver,
                        path,
                        NpcVisualAssetRole.Texture,
                        materialized,
                        diagnostics,
                        required: false,
                        cancellationToken);
                if (texture is null) continue;
                slots.Add(new(
                    slot,
                    TextureSemantic(slot),
                    texture.AssetPath,
                    texture.Provider,
                    texture.Sha256,
                    texture.MaterializedPath));
            }
            materials.Add(new NpcVisualMaterial(
                descriptor.Shape,
                descriptor.ShaderType,
                slots.ToImmutable(),
                descriptor.ShaderFlags1,
                descriptor.ShaderFlags2,
                descriptor.HasAlpha,
                descriptor.Alpha,
                descriptor.TintHex));
        }
        if (materials.Count == 0)
            diagnostics.Add(Warning(
                "npc-preview-materials-missing",
                $"NIF '{nifLabel}' exposes no material texture set; the preview will show a diagnostic fallback."));
        return materials.ToImmutable();
    }

    private static async ValueTask<NpcVisualAsset?>
        MaterializeSimpleAsync(
            PreviewAssetResolver resolver,
            string requestedPath,
            NpcVisualAssetRole role,
            Dictionary<string, NpcVisualAsset> materialized,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            bool required,
            CancellationToken cancellationToken)
    {
        string normalized = NormalizeAssetPath(requestedPath);
        if (materialized.TryGetValue(
                normalized, out NpcVisualAsset? existing))
            return existing;
        ResolvedPreviewAsset? resolved =
            await resolver.ResolveAsync(
                normalized, cancellationToken);
        if (resolved is null)
        {
            diagnostics.Add(required
                ? Error(
                    "npc-preview-asset-missing",
                    $"Asset '{normalized}' was not resolved from the package overlay or reviewed copied providers.")
                : Warning(
                    "npc-preview-material-missing",
                    $"Asset '{normalized}' was not resolved from the package overlay or reviewed copied providers."));
            return null;
        }
        var asset = new NpcVisualAsset(
            role,
            new AssetPath(normalized),
            resolved.Provider,
            resolved.Sha256,
            resolved.Bytes,
            resolved.MaterializedPath,
            false,
            []);
        materialized[normalized] = asset;
        return asset;
    }

    private static ImmutableArray<ImmutableArray<string>>
        FindTextureSets(ReadOnlySpan<byte> bytes)
    {
        var results =
            ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int offset = 0; offset <= bytes.Length - 8; offset++)
        {
            uint count = BitConverter.ToUInt32(
                bytes.Slice(offset, 4));
            if (count is 0 or > 16) continue;
            int cursor = offset + 4;
            var paths = ImmutableArray.CreateBuilder<string>(
                checked((int)count));
            bool valid = true;
            bool hasTexture = false;
            for (int index = 0; index < count; index++)
            {
                if (cursor > bytes.Length - 4)
                {
                    valid = false;
                    break;
                }
                uint length = BitConverter.ToUInt32(
                    bytes.Slice(cursor, 4));
                cursor += 4;
                if (length > 1024 ||
                    cursor > bytes.Length - length)
                {
                    valid = false;
                    break;
                }
                string value = Encoding.UTF8.GetString(
                    bytes.Slice(cursor, checked((int)length)));
                cursor += checked((int)length);
                if (value.Any(character =>
                        character < 0x20 ||
                        character > 0x7E))
                {
                    valid = false;
                    break;
                }
                if (!string.IsNullOrEmpty(value))
                {
                    if (!value.EndsWith(
                            ".dds",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        valid = false;
                        break;
                    }
                    hasTexture = true;
                }
                paths.Add(value);
            }
            if (!valid || !hasTexture) continue;
            string key = string.Join(
                "\u001F", paths);
            if (seen.Add(key))
                results.Add(paths.ToImmutable());
        }
        return results.ToImmutable();
    }

    private static async ValueTask<ImmutableArray<NpcVisualMorph>>
        ReadBodyGenMorphsAsync(
            string providerName,
            uint formId,
            PreviewAssetResolver resolver,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        const int maximumLines = 16384;
        const int maximumTemplates = 256;
        const int maximumMorphs = 4096;
        string morphsPath =
            $"meshes/actors/character/BodyGenData/{providerName}/morphs.ini";
        ResolvedPreviewAsset? morphsAsset =
            await resolver.ResolveAsync(morphsPath, cancellationToken);
        if (morphsAsset is null) return [];
        string[] mappingLines;
        try
        {
            mappingLines = await File.ReadAllLinesAsync(
                morphsAsset.MaterializedPath.Value,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException)
        {
            diagnostics.Add(Error(
                "npc-preview-bodygen-morphs-read-failed",
                $"BodyGen mappings could not be read: {exception.Message}"));
            return [];
        }
        if (mappingLines.Length > maximumLines)
        {
            diagnostics.Add(Error(
                "npc-preview-bodygen-morphs-too-large",
                $"BodyGen mappings contain {mappingLines.Length} lines; the preview limit is {maximumLines}."));
            return [];
        }
        string id8 = formId.ToString("X8");
        string id6 = formId.ToString("X6");
        var templateNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string line in mappingLines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith(';') ||
                trimmed.StartsWith('#'))
                continue;
            int equals = trimmed.IndexOf('=');
            if (equals <= 0) continue;
            string owner = trimmed[..equals].Trim();
            int separator = owner.LastIndexOf('|');
            string ownerId =
                separator >= 0
                    ? owner[(separator + 1)..].Trim()
                    : owner;
            ownerId = ownerId.StartsWith(
                    "0x", StringComparison.OrdinalIgnoreCase)
                ? ownerId[2..]
                : ownerId;
            if (!string.Equals(
                    ownerId, id8,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    ownerId, id6,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            string value = trimmed[(equals + 1)..].Trim();
            if (value.Length == 0) continue;
            foreach (string group in value.Split(
                         '|',
                         StringSplitOptions.TrimEntries |
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (!templateNames.Add(group)) continue;
                if (templateNames.Count > maximumTemplates)
                {
                    diagnostics.Add(Error(
                        "npc-preview-bodygen-template-count",
                        $"BodyGen selects more than {maximumTemplates} templates for the actor."));
                    return [];
                }
            }
        }
        if (templateNames.Count == 0) return [];

        string templatesPath =
            $"meshes/actors/character/BodyGenData/{providerName}/templates.ini";
        ResolvedPreviewAsset? templatesAsset =
            await resolver.ResolveAsync(templatesPath, cancellationToken);
        if (templatesAsset is null)
        {
            diagnostics.Add(Error(
                "npc-preview-bodygen-templates-missing",
                $"BodyGen selects {templateNames.Count} template(s), but '{templatesPath}' is unavailable."));
            return [];
        }
        string[] templateLines;
        try
        {
            templateLines = await File.ReadAllLinesAsync(
                templatesAsset.MaterializedPath.Value,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException)
        {
            diagnostics.Add(Error(
                "npc-preview-bodygen-templates-read-failed",
                $"BodyGen templates could not be read: {exception.Message}"));
            return [];
        }
        if (templateLines.Length > maximumLines)
        {
            diagnostics.Add(Error(
                "npc-preview-bodygen-templates-too-large",
                $"BodyGen templates contain {templateLines.Length} lines; the preview limit is {maximumLines}."));
            return [];
        }

        var morphs = ImmutableArray.CreateBuilder<NpcVisualMorph>();
        var foundTemplates = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var seenMorphs = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string line in templateLines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith(';') ||
                trimmed.StartsWith('#'))
                continue;
            int equals = trimmed.IndexOf('=');
            if (equals <= 0) continue;
            string templateName = trimmed[..equals].Trim();
            if (!templateNames.Contains(templateName)) continue;
            if (!foundTemplates.Add(templateName))
            {
                diagnostics.Add(Error(
                    "npc-preview-bodygen-template-duplicate",
                    $"BodyGen template '{templateName}' is declared more than once."));
                continue;
            }
            string assignments = trimmed[(equals + 1)..].Trim();
            foreach (string assignment in assignments.Split(
                         ',',
                         StringSplitOptions.TrimEntries |
                         StringSplitOptions.RemoveEmptyEntries))
            {
                int at = assignment.LastIndexOf('@');
                if (at <= 0 ||
                    at == assignment.Length - 1 ||
                    !float.TryParse(
                        assignment[(at + 1)..],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float value) ||
                    !float.IsFinite(value))
                {
                    diagnostics.Add(Error(
                        "npc-preview-bodygen-morph-invalid",
                        $"BodyGen template '{templateName}' contains invalid assignment '{assignment}'."));
                    continue;
                }
                string morphName = assignment[..at].Trim();
                string key = $"{templateName}\u001F{morphName}";
                if (morphName.Length == 0 ||
                    !seenMorphs.Add(key))
                {
                    diagnostics.Add(Error(
                        "npc-preview-bodygen-morph-duplicate",
                        $"BodyGen template '{templateName}' contains an empty or duplicate morph '{morphName}'."));
                    continue;
                }
                morphs.Add(new(
                    $"BodyGen:{templateName}",
                    morphName,
                    value,
                    templatesAsset.Provider,
                    new AssetPath(templatesPath),
                    templatesAsset.Sha256));
                if (morphs.Count > maximumMorphs)
                {
                    diagnostics.Add(Error(
                        "npc-preview-bodygen-morph-count",
                        $"BodyGen resolves to more than {maximumMorphs} morph assignments."));
                    return [];
                }
            }
        }
        foreach (string missing in templateNames.Except(
                     foundTemplates,
                     StringComparer.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "npc-preview-bodygen-template-unresolved",
                $"BodyGen template '{missing}' is selected for the actor but is not declared."));
        return morphs.ToImmutable();
    }

    private static async ValueTask AddBodyMorphTriAssetsAsync(
        ImmutableArray<NpcVisualMorph> morphs,
        PreviewAssetResolver resolver,
        Dictionary<string, NpcVisualAsset> materialized,
        ImmutableArray<NpcVisualAsset>.Builder assets,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (morphs.IsDefaultOrEmpty)
            return;

        int resolvedCount = 0;
        ImmutableArray<NpcVisualAsset> morphableMeshes =
            assets
                .Where(item =>
                    item.Role is
                        NpcVisualAssetRole.Body or
                        NpcVisualAssetRole.Hands or
                        NpcVisualAssetRole.Feet or
                        NpcVisualAssetRole.Outfit)
                .DistinctBy(
                    item => item.AssetPath.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        foreach (NpcVisualAsset mesh in morphableMeshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string meshPath = mesh.AssetPath.Value;
            string triPath = meshPath.EndsWith(
                    "_1.nif",
                    StringComparison.OrdinalIgnoreCase)
                ? meshPath[..^"_1.nif".Length] + ".tri"
                : meshPath.EndsWith(
                    ".nif",
                    StringComparison.OrdinalIgnoreCase)
                    ? meshPath[..^".nif".Length] + ".tri"
                    : "";
            if (triPath.Length == 0)
                continue;
            string normalized = NormalizeAssetPath(triPath);
            if (materialized.TryGetValue(
                    normalized,
                    out NpcVisualAsset? existing))
            {
                if (existing.Role == NpcVisualAssetRole.Tri)
                {
                    assets.Add(existing);
                    resolvedCount++;
                }
                continue;
            }

            ResolvedPreviewAsset? resolved =
                await resolver.ResolveAsync(
                    normalized,
                    cancellationToken);
            if (resolved is null)
            {
                diagnostics.Add(Warning(
                    "npc-preview-bodygen-tri-missing",
                    $"BodyGen assignments are present, but mesh '{mesh.AssetPath}' has no resolved adjacent PIRT '{normalized}'."));
                continue;
            }
            var tri = new NpcVisualAsset(
                NpcVisualAssetRole.Tri,
                new AssetPath(normalized),
                resolved.Provider,
                resolved.Sha256,
                resolved.Bytes,
                resolved.MaterializedPath,
                false,
                []);
            materialized[normalized] = tri;
            assets.Add(tri);
            resolvedCount++;
        }

        if (resolvedCount == 0)
            diagnostics.Add(Warning(
                "npc-preview-bodygen-runtime-morph-unapplied",
                $"Resolved {morphs.Length} BodyGen slider assignment(s), but no admitted adjacent PIRT geometry is available; the off-engine mesh remains at its packaged weight shape."));
    }

    private static NpcVisualAssetRole ClassifyBodyRole(
        string path)
    {
        if (path.Contains(
                "hand", StringComparison.OrdinalIgnoreCase))
            return NpcVisualAssetRole.Hands;
        if (path.Contains(
                "feet", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(
                "foot", StringComparison.OrdinalIgnoreCase))
            return NpcVisualAssetRole.Feet;
        return NpcVisualAssetRole.Body;
    }

    private static string NormalizeMeshPath(string value)
    {
        string normalized =
            NormalizeAssetPath(value);
        if (!normalized.StartsWith(
                "meshes/", StringComparison.OrdinalIgnoreCase))
            normalized = "meshes/" + normalized;
        return normalized;
    }

    private static string NormalizeTexturePath(string value)
    {
        string normalized =
            NormalizeAssetPath(value);
        if (!normalized.StartsWith(
                "textures/", StringComparison.OrdinalIgnoreCase))
            normalized = "textures/" + normalized;
        if (!normalized.EndsWith(
                ".dds", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Texture '{value}' is not a DDS path.");
        return normalized;
    }

    private static string TextureSemantic(int slot) =>
        slot switch
        {
            0 => "diffuse",
            1 => "normal",
            2 => "glow-detail-softlighting",
            3 => "height-greyscale",
            4 => "environment",
            5 => "environment-mask",
            6 => "facetint-innerlayer",
            7 => "specular-backlight",
            _ => $"slot-{slot}"
        };

    private sealed record ResolvedPreviewAsset(
        string Provider,
        Sha256Hash Sha256,
        long Bytes,
        WorkspacePath MaterializedPath);

    private sealed record PreviewAssetWinner(
        AssetProviderKind Kind,
        string Provider,
        WorkspacePath SourcePath,
        Sha256Hash Sha256,
        long Bytes);

    private sealed record PreviewAssetAuthorityPlan(
        ImmutableDictionary<string, PreviewAssetWinner> Winners);

    private sealed class PreviewAssetResolver(
        WorkspacePath dataRoot,
        PackageOverlay? overlayPackage,
        WorkspacePath outputDataRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        PreviewAssetAuthorityPlan? authorityPlan = null,
        long memberLimitBytes =
            512L * 1024L * 1024L)
    {
        private const long MaximumCumulativeBytes =
            2L * 1024 * 1024 * 1024;
        private readonly Dictionary<string, ResolvedPreviewAsset> cache =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> missing =
            new(StringComparer.OrdinalIgnoreCase);
        private long cumulativeBytes;

        public async ValueTask<ResolvedPreviewAsset?> ResolveAsync(
            string requestedPath,
            CancellationToken cancellationToken)
        {
            string normalized =
                NormalizeAssetPath(requestedPath);
            if (cache.TryGetValue(
                    normalized, out ResolvedPreviewAsset? cached))
                return cached;
            if (missing.Contains(normalized))
                return null;
            string relative = normalized.Replace(
                '/', Path.DirectorySeparatorChar);
            PreviewAssetWinner? expectedWinner = null;
            if (authorityPlan is not null &&
                !authorityPlan.Winners.TryGetValue(
                    normalized,
                    out expectedWinner))
            {
                diagnostics.Add(Error(
                    "npc-preview-asset-authority-missing",
                    $"Reviewed intake has no unambiguous load-order winner for '{normalized}'."));
                missing.Add(normalized);
                return null;
            }
            if (overlayPackage is { } package)
            {
                WorkspacePath overlay = package.DataRoot;
                string path = Path.GetFullPath(
                    Path.Combine(overlay.Value, relative));
                string declaredRelative =
                    $"Data/{normalized}";
                PackageFile? authority =
                    package.Files.FirstOrDefault(item =>
                        string.Equals(
                            item.RelativePath.Replace('\\', '/'),
                            declaredRelative,
                            StringComparison.OrdinalIgnoreCase));
                if (authority is not null &&
                    new WorkspacePath(path).IsUnder(overlay) &&
                    File.Exists(path) &&
                    !Directory.Exists(path) &&
                    IsTrustedReadPath(path, "package asset"))
                    return await TryMaterializeLooseAsync(
                        normalized,
                        path,
                        $"package-overlay:{Path.GetFileName(package.Root.Value)}",
                        authority,
                        null,
                        cancellationToken);
            }
            string loose = Path.GetFullPath(
                Path.Combine(dataRoot.Value, relative));
            if (new WorkspacePath(loose).IsUnder(dataRoot) &&
                File.Exists(loose) &&
                !Directory.Exists(loose) &&
                IsTrustedReadPath(loose, "loose asset"))
            {
                if (expectedWinner is not null &&
                    expectedWinner.Kind !=
                        AssetProviderKind.Loose)
                {
                    diagnostics.Add(Error(
                        "npc-preview-asset-authority-drift",
                        $"Loose asset '{normalized}' exists but the reviewed winner is '{expectedWinner.Provider}'."));
                    return null;
                }
                return await TryMaterializeLooseAsync(
                    normalized,
                    loose,
                    expectedWinner?.Provider ??
                    $"loose:{dataRoot.Value}",
                    null,
                    expectedWinner,
                    cancellationToken);
            }

            string[] archives = expectedWinner is not null
                ? expectedWinner.Kind ==
                    AssetProviderKind.Archive
                    ? [expectedWinner.SourcePath.Value]
                    : []
                : Directory.GetFiles(
                        dataRoot.Value,
                        "*.bsa",
                        SearchOption.TopDirectoryOnly)
                    .OrderByDescending(
                        path => path,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            foreach (string archivePath in archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsTrustedReadPath(
                        archivePath, "archive asset provider"))
                    return null;
                try
                {
                    var reader = Archive.CreateReader(
                        GameRelease.SkyrimSE,
                        new FilePath(archivePath),
                        new FileSystem());
                    IArchiveFile? entry = reader.Files.FirstOrDefault(
                        item => string.Equals(
                            NormalizeAssetPath(item.Path),
                            normalized,
                            StringComparison.OrdinalIgnoreCase));
                    if (entry is null) continue;
                    if (entry.Size is 0 ||
                        entry.Size > memberLimitBytes ||
                        cumulativeBytes + entry.Size >
                        MaximumCumulativeBytes)
                    {
                        diagnostics.Add(Error(
                            "npc-preview-asset-size",
                            $"Archive asset '{normalized}' exceeds the bounded preview budget."));
                        return null;
                    }
                    string destination = Destination(normalized);
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(destination)!);
                    using IncrementalHash hash =
                        IncrementalHash.CreateHash(
                            HashAlgorithmName.SHA256);
                    long written = 0;
                    {
                        await using FileStream output = new(
                            destination,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            64 * 1024,
                            FileOptions.Asynchronous |
                            FileOptions.SequentialScan);
                        using Stream input = entry.AsStream();
                        byte[] buffer =
                            ArrayPool<byte>.Shared.Rent(64 * 1024);
                        try
                        {
                            while (true)
                            {
                                int read = await input.ReadAsync(
                                    buffer.AsMemory(0, 64 * 1024),
                                    cancellationToken);
                                if (read == 0) break;
                                written += read;
                                if (written > entry.Size)
                                    throw new InvalidDataException(
                                        "Archive member exceeded its declared length.");
                                hash.AppendData(buffer, 0, read);
                                await output.WriteAsync(
                                    buffer.AsMemory(0, read),
                                    cancellationToken);
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                        await output.FlushAsync(cancellationToken);
                    }
                    if (written != entry.Size)
                        throw new InvalidDataException(
                            "Archive member length differs from its declaration.");
                    var streamedHash = new Sha256Hash(
                        Convert.ToHexString(
                            hash.GetHashAndReset()));
                    Sha256Hash archiveOutputHash =
                        await BethesdaNpcVisualSourceComposer.HashFileAsync(
                            new WorkspacePath(destination),
                            cancellationToken);
                    if (streamedHash != archiveOutputHash)
                        throw new InvalidDataException(
                            "Archive member failed independent copy readback.");
                    if (expectedWinner is not null &&
                        (expectedWinner.Kind !=
                             AssetProviderKind.Archive ||
                         !string.Equals(
                             expectedWinner.SourcePath.Value,
                             archivePath,
                             StringComparison.OrdinalIgnoreCase) ||
                         expectedWinner.Bytes != written ||
                         expectedWinner.Sha256 != streamedHash))
                        throw new InvalidDataException(
                            $"Archive winner for '{normalized}' differs from the reviewed provider inventory.");
                    var result = new ResolvedPreviewAsset(
                        expectedWinner?.Provider ??
                        $"bsa:{Path.GetFileName(archivePath)}",
                        streamedHash,
                        written,
                        new WorkspacePath(destination));
                    cumulativeBytes += written;
                    cache[normalized] = result;
                    return result;
                }
                catch (Exception exception) when (
                    exception is IOException or
                        UnauthorizedAccessException or
                        InvalidDataException or
                        ArgumentException)
                {
                    diagnostics.Add(Error(
                        "npc-preview-archive-read",
                        $"Archive '{Path.GetFileName(archivePath)}' could not provide '{normalized}': {exception.Message}"));
                    return null;
                }
            }
            missing.Add(normalized);
            return null;
        }

        private bool IsTrustedReadPath(
            string path,
            string role)
        {
            ImmutableArray<Diagnostic> pathDiagnostics =
                policy.EvaluateReadRoot(
                    labRoot,
                    new WorkspacePath(path));
            diagnostics.AddRange(pathDiagnostics);
            if (!pathDiagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error))
                return true;
            if (pathDiagnostics.Any(item =>
                    item.Code == "reparse-point-refused"))
                diagnostics.Add(Error(
                    "npc-preview-asset-reparse-refused",
                    $"The {role} traverses a reparse point."));
            return false;
        }

        private async ValueTask<ResolvedPreviewAsset?>
            TryMaterializeLooseAsync(
                string normalized,
                string source,
                string provider,
                PackageFile? expectedAuthority,
                PreviewAssetWinner? expectedWinner,
                CancellationToken cancellationToken)
        {
            try
            {
                return await MaterializeLooseAsync(
                    normalized,
                    source,
                    provider,
                    expectedAuthority,
                    expectedWinner,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException)
            {
                diagnostics.Add(Error(
                    "npc-preview-asset-read",
                    $"Asset '{normalized}' could not be materialized: {exception.Message}"));
                return null;
            }
        }

        private async ValueTask<ResolvedPreviewAsset>
            MaterializeLooseAsync(
                string normalized,
                string source,
                string provider,
                PackageFile? expectedAuthority,
                PreviewAssetWinner? expectedWinner,
                CancellationToken cancellationToken)
        {
            var info = new FileInfo(source);
            if (info.Length is <= 0 ||
                info.Length > memberLimitBytes ||
                cumulativeBytes + info.Length >
                MaximumCumulativeBytes)
                throw new InvalidDataException(
                    $"Asset '{normalized}' exceeds the bounded preview budget.");
            string destination = Destination(normalized);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)!);
            using IncrementalHash hash =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
            long written = 0;
            {
                await using FileStream input = new(
                    source,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);
                await using FileStream output = new(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);
                byte[] buffer =
                    ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (true)
                    {
                        int read = await input.ReadAsync(
                            buffer.AsMemory(0, 64 * 1024),
                            cancellationToken);
                        if (read == 0) break;
                        written += read;
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                await output.FlushAsync(cancellationToken);
            }
            if (written != info.Length)
                throw new InvalidDataException(
                    $"Asset '{normalized}' changed while it was copied.");
            Sha256Hash sourceHash = new(
                Convert.ToHexString(hash.GetHashAndReset()));
            Sha256Hash outputHash =
                await BethesdaNpcVisualSourceComposer.HashFileAsync(
                    new WorkspacePath(destination),
                    cancellationToken);
            if (sourceHash != outputHash)
                throw new InvalidDataException(
                    $"Asset '{normalized}' failed independent copy readback.");
            if (expectedAuthority is not null &&
                (written != expectedAuthority.ByteLength ||
                 sourceHash != expectedAuthority.Sha256))
                throw new InvalidDataException(
                    $"Package asset '{normalized}' changed after its manifest was admitted.");
            if (expectedWinner is not null &&
                (expectedWinner.Kind !=
                     AssetProviderKind.Loose ||
                 !string.Equals(
                     expectedWinner.SourcePath.Value,
                     source,
                     StringComparison.OrdinalIgnoreCase) ||
                 expectedWinner.Bytes != written ||
                 expectedWinner.Sha256 != sourceHash))
                throw new InvalidDataException(
                    $"Loose winner for '{normalized}' differs from the reviewed provider inventory.");
            var result = new ResolvedPreviewAsset(
                provider,
                sourceHash,
                written,
                new WorkspacePath(destination));
            cumulativeBytes += written;
            cache[normalized] = result;
            return result;
        }

        private string Destination(string normalized)
        {
            string value = Path.GetFullPath(Path.Combine(
                outputDataRoot.Value,
                normalized.Replace(
                    '/', Path.DirectorySeparatorChar)));
            var destination = new WorkspacePath(value);
            if (!destination.IsUnder(outputDataRoot))
                throw new InvalidDataException(
                    "Resolved asset destination escapes the preview root.");
            return value;
        }
    }
}
