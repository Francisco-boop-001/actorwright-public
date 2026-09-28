using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Reopens a persisted whole-skin manifest, then independently re-resolves the
/// record and asset graph before accepting the declared snapshot.
/// </summary>
public sealed class RaceMenuNpcWholeSkinAuthorityReader(
    ISkyrimNpcWholeSkinAuthorityResolver resolver,
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot)
    : IRaceMenuNpcWholeSkinAuthorityReader
{
    private const int MaximumManifestBytes = 2 * 1024 * 1024;

    public async ValueTask<RaceMenuNpcWholeSkinAuthorityReadResult> ReadAsync(
        RaceMenuNpcWholeSkinAuthorityReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(
            labRoot, request.Authority.ManifestPath));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.Authority.ManifestPath.Value);
            if (!info.Exists || info.Length is <= 0 or > MaximumManifestBytes ||
                info.Attributes.HasFlag(FileAttributes.Directory) ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("racemenu-whole-skin-authority-file",
                    "Whole-skin authority must be one bounded ordinary K-local file."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(
                request.Authority.ManifestPath.Value,
                cancellationToken).ConfigureAwait(false);
            var actualHash = new Sha256Hash(
                Convert.ToHexString(SHA256.HashData(bytes)));
            if (actualHash != request.Authority.ExpectedManifestSha256)
            {
                diagnostics.Add(Error("racemenu-whole-skin-authority-hash",
                    $"Whole-skin authority hash {actualHash} does not match {request.Authority.ExpectedManifestSha256}."));
                return Refused(diagnostics);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-file",
                exception.Message));
            return Refused(diagnostics);
        }

        ParsedAuthority parsed;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 20
                });
            RejectDuplicateKeys(document.RootElement);
            parsed = Parse(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or
                                           InvalidDataException or
                                           ArgumentException or
                                           OverflowException)
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-invalid",
                exception.Message));
            return Refused(diagnostics);
        }

        if (parsed.Snapshot.Race != request.ExpectedRace ||
            parsed.Sex != request.ExpectedSex)
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-target",
                "Whole-skin authority race or sex does not match the new NPC request."));
            return Refused(diagnostics);
        }

        SkyrimNpcWholeSkinAuthorityResult current =
            await resolver.ResolveAsync(
                new SkyrimNpcWholeSkinAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    parsed.DataRoot,
                    parsed.Snapshot.Race,
                    parsed.Sex,
                    parsed.PluginOrder)
                {
                    DefaultOutfit = request.ExpectedDefaultOutfit,
                    AllowMeshEmbeddedSkinTextureRoute =
                        parsed.Snapshot.Regions.Any(item =>
                            item.TextureSet is null),
                    BodyMeshAuthority = request.BodyMeshAuthority
                },
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(current.Diagnostics);
        if (!current.Accepted || current.Authority is null)
            return Refused(diagnostics);
        if (!string.Equals(
                SemanticKey(parsed.Snapshot),
                SemanticKey(current.Authority),
                StringComparison.Ordinal))
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-stale",
                "The persisted whole-skin graph no longer matches independently re-resolved record or texture providers."));
            return Refused(diagnostics);
        }
        diagnostics.Add(new Diagnostic(
            "racemenu-whole-skin-authority-verified",
            DiagnosticSeverity.Info,
            "The complete inherited race skin manifest and its body/hands/feet providers were independently re-resolved."));
        return new RaceMenuNpcWholeSkinAuthorityReadResult(
            true, current.Authority, diagnostics.ToImmutable());
    }

    private ParsedAuthority Parse(JsonElement root)
    {
        int schemaVersion = RequiredInt(root, "schemaVersion");
        if (schemaVersion == 1)
        {
            RequireShape(root, "whole-skin authority",
                "schemaVersion", "edition", "dataRoot", "race", "sex", "route",
                "runtimeAuthority", "pluginOrder", "raceBinding", "skinArmor",
                "regions");
        }
        else if (schemaVersion == 2)
        {
            RequireShape(root, "whole-skin authority",
                "schemaVersion", "edition", "dataRoot", "race", "sex", "route",
                "runtimeAuthority", "pluginOrder", "raceBinding", "skinArmor",
                "regions", "exposedOutfitSkinBinding");
        }
        else if (schemaVersion == 3)
        {
            RequireShape(root, "whole-skin authority",
                "schemaVersion", "edition", "dataRoot", "race", "sex", "route",
                "runtimeAuthority", "pluginOrder", "raceBinding", "skinArmor",
                "regions", "exposedOutfitSkinBinding");
        }
        else
        {
            throw new InvalidDataException(
                "Whole-skin authority schema is unsupported.");
        }
        if (
            !string.Equals(RequiredString(root, "edition"), "skyrimse",
                StringComparison.Ordinal) ||
            !string.Equals(RequiredString(root, "route"), "inherited-race",
                StringComparison.Ordinal) ||
            RequiredBool(root, "runtimeAuthority"))
        {
            throw new InvalidDataException(
                "Whole-skin authority requires Skyrim SE, inherited-race routing, and runtimeAuthority false.");
        }
        if (!FormReference.TryParse(RequiredString(root, "race"), out var race) ||
            race.FormId.Value is 0 or > 0x00FF_FFFF)
            throw new InvalidDataException("Whole-skin race is invalid.");
        NpcSex sex = RequiredString(root, "sex") switch
        {
            "female" => NpcSex.Female,
            _ => throw new InvalidDataException(
                "Whole-skin authority currently supports female NPCs only.")
        };
        WorkspacePath dataRoot = ResolveRelative(
            RequiredString(root, "dataRoot"), "dataRoot");

        JsonElement plugins = root.GetProperty("pluginOrder");
        if (plugins.ValueKind != JsonValueKind.Array ||
            plugins.GetArrayLength() is <= 0 or > 64)
            throw new InvalidDataException(
                "Whole-skin pluginOrder must contain 1-64 entries.");
        var pluginOrder = ImmutableArray.CreateBuilder<
            SkyrimFaceRecordPluginAuthority>();
        foreach (JsonElement plugin in plugins.EnumerateArray())
        {
            RequireShape(plugin, "whole-skin plugin",
                "plugin", "path", "sha256");
            pluginOrder.Add(new SkyrimFaceRecordPluginAuthority(
                new PluginName(RequiredString(plugin, "plugin")),
                ResolveRelative(RequiredString(plugin, "path"), "plugin path"),
                new Sha256Hash(RequiredString(plugin, "sha256"))));
        }
        if (pluginOrder.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            pluginOrder.Count)
            throw new InvalidDataException(
                "Whole-skin pluginOrder repeats a plugin.");

        RaceMenuNpcFormBinding raceBinding = ReadBinding(
            root.GetProperty("raceBinding"), new RecordSignature("RACE"));
        RaceMenuNpcFormBinding skinArmor = ReadBinding(
            root.GetProperty("skinArmor"), new RecordSignature("ARMO"));
        if (raceBinding.Reference != race)
            throw new InvalidDataException(
                "Whole-skin race binding does not match the declared race.");

        JsonElement rows = root.GetProperty("regions");
        if (rows.ValueKind != JsonValueKind.Array ||
            rows.GetArrayLength() != 3)
            throw new InvalidDataException(
                "Whole-skin authority requires exactly three regions.");
        var regions = ImmutableArray.CreateBuilder<SkyrimNpcSkinRegionAuthority>(3);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (schemaVersion == 3)
            {
                RequireShape(row, "whole-skin region",
                    "region", "textureRoute", "slotMask", "armorAddon",
                    "textureSet", "textures", "textureAssets");
            }
            else
            {
                RequireShape(row, "whole-skin region",
                    "region", "slotMask", "armorAddon", "textureSet",
                    "textures", "textureAssets");
            }
            SkyrimNpcSkinRegion region = RequiredString(row, "region") switch
            {
                "body" => SkyrimNpcSkinRegion.Body,
                "hands" => SkyrimNpcSkinRegion.Hands,
                "feet" => SkyrimNpcSkinRegion.Feet,
                _ => throw new InvalidDataException(
                    "Whole-skin region is unsupported.")
            };
            uint slotMask = checked((uint)RequiredInt(row, "slotMask"));
            RaceMenuNpcFormBinding addon = ReadBinding(
                row.GetProperty("armorAddon"), new RecordSignature("ARMA"));
            string textureRoute = schemaVersion == 3
                ? RequiredString(row, "textureRoute")
                : "txst";
            RaceMenuNpcFormBinding? textureSet = null;
            SkyrimNpcSkinTexturePaths? texturePaths = null;
            JsonElement textureElement = row.GetProperty("textureSet");
            JsonElement paths = row.GetProperty("textures");
            if (textureRoute == "txst")
            {
                textureSet = ReadBinding(
                    textureElement, new RecordSignature("TXST"));
                RequireShape(paths, "whole-skin textures",
                    "diffuse", "normal", "subsurface", "specular");
                texturePaths = new SkyrimNpcSkinTexturePaths(
                    TexturePath(paths, "diffuse"),
                    TexturePath(paths, "normal"),
                    TexturePath(paths, "subsurface"),
                    TexturePath(paths, "specular"));
            }
            else if (textureRoute == "mesh-embedded")
            {
                if (schemaVersion != 3 ||
                    textureElement.ValueKind != JsonValueKind.Null ||
                    paths.ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException(
                        "Mesh-embedded whole-skin regions must declare null textureSet and textures.");
            }
            else
            {
                throw new InvalidDataException(
                    "Whole-skin textureRoute is unsupported.");
            }
            JsonElement assets = row.GetProperty("textureAssets");
            if (assets.ValueKind != JsonValueKind.Array ||
                textureRoute == "txst" && assets.GetArrayLength() != 4 ||
                textureRoute == "mesh-embedded" &&
                assets.GetArrayLength() is <= 0 or > 16)
                throw new InvalidDataException(
                    textureRoute == "txst"
                        ? "Each TXST whole-skin region requires four texture assets."
                        : "Each mesh-embedded whole-skin region requires 1-16 texture assets.");
            var textureAssets = ImmutableArray.CreateBuilder<SkyrimAssetAuthority>(
                assets.GetArrayLength());
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                RequireShape(asset, "whole-skin texture asset",
                    "providerId", "providerKind", "providerPath",
                    "providerSha256", "assetPath", "contentLength",
                    "contentSha256");
                AssetProviderKind kind =
                    RequiredString(asset, "providerKind") switch
                    {
                        "loose" => AssetProviderKind.Loose,
                        "archive" => AssetProviderKind.Archive,
                        _ => throw new InvalidDataException(
                            "Whole-skin providerKind is unsupported.")
                    };
                long length = RequiredLong(asset, "contentLength");
                if (length <= 0)
                    throw new InvalidDataException(
                        "Whole-skin texture contentLength must be positive.");
                textureAssets.Add(new SkyrimAssetAuthority(
                    RequiredString(asset, "providerId"),
                    kind,
                    ResolveRelative(
                        RequiredString(asset, "providerPath"),
                        "texture provider path"),
                    new Sha256Hash(RequiredString(asset, "providerSha256")),
                    new AssetPath(RequiredString(asset, "assetPath")),
                    length,
                    new Sha256Hash(RequiredString(asset, "contentSha256"))));
            }
            regions.Add(new SkyrimNpcSkinRegionAuthority(
                region, slotMask, addon, textureSet, texturePaths,
                textureAssets.ToImmutable()));
        }
        if (!regions.Select(item => item.Region)
                .SequenceEqual(new[]
                {
                    SkyrimNpcSkinRegion.Body,
                    SkyrimNpcSkinRegion.Hands,
                    SkyrimNpcSkinRegion.Feet
                }))
            throw new InvalidDataException(
                "Whole-skin regions must be ordered body, hands, feet.");

        SkyrimNpcExposedOutfitSkinBinding? exposedOutfitSkinBinding = null;
        if (schemaVersion == 2)
        {
            JsonElement outfitElement =
                root.GetProperty("exposedOutfitSkinBinding");
            if (outfitElement.ValueKind != JsonValueKind.Null)
            {
                RequireShape(outfitElement,
                    "whole-skin exposed outfit binding",
                    "outfit", "armor", "armorAddon",
                    "targetFemaleSkinTextureSet", "exposedSlotMask");
                exposedOutfitSkinBinding =
                    new SkyrimNpcExposedOutfitSkinBinding(
                        ReadBinding(
                            outfitElement.GetProperty("outfit"),
                            new RecordSignature("OTFT")),
                        ReadBinding(
                            outfitElement.GetProperty("armor"),
                            new RecordSignature("ARMO")),
                        ReadBinding(
                            outfitElement.GetProperty("armorAddon"),
                            new RecordSignature("ARMA")),
                        ReadBinding(
                            outfitElement.GetProperty(
                                "targetFemaleSkinTextureSet"),
                            new RecordSignature("TXST")),
                        checked((uint)RequiredInt(
                            outfitElement, "exposedSlotMask")));
                RaceMenuNpcFormBinding? bodyTextureSet = regions.Single(
                        item => item.Region == SkyrimNpcSkinRegion.Body)
                    .TextureSet;
                if (exposedOutfitSkinBinding.ExposedSlotMask != 0x04U ||
                    bodyTextureSet is null ||
                    exposedOutfitSkinBinding.TargetFemaleSkinTextureSet.Reference !=
                    bodyTextureSet.Reference)
                    throw new InvalidDataException(
                        "Whole-skin exposed outfit binding does not target the accepted body TXST.");
            }
        }

        return new ParsedAuthority(
            dataRoot,
            sex,
            pluginOrder.ToImmutable(),
            new SkyrimNpcWholeSkinAuthority(
                SkyrimNpcSkinRouteKind.InheritedRace,
                race,
                raceBinding,
                skinArmor,
                regions.ToImmutable(),
                RuntimeAuthority: false)
            {
                ExposedOutfitSkinBinding = exposedOutfitSkinBinding
            });
    }

    private RaceMenuNpcFormBinding ReadBinding(
        JsonElement element,
        RecordSignature expectedSignature)
    {
        RequireShape(element, "whole-skin form binding",
            "signature", "sourceFormKey", "providerFormKey",
            "providerPluginName", "providerPluginPath",
            "providerPluginSha256");
        var signature = new RecordSignature(
            RequiredString(element, "signature"));
        if (signature != expectedSignature ||
            !FormReference.TryParse(
                RequiredString(element, "sourceFormKey"), out var source) ||
            !FormReference.TryParse(
                RequiredString(element, "providerFormKey"), out var provider))
            throw new InvalidDataException(
                $"Whole-skin {expectedSignature} binding is invalid.");
        return new RaceMenuNpcFormBinding(
            signature,
            source,
            provider,
            new PluginName(RequiredString(element, "providerPluginName")),
            ResolveRelative(
                RequiredString(element, "providerPluginPath"),
                "record provider path"),
            new Sha256Hash(
                RequiredString(element, "providerPluginSha256")),
            null);
    }

    private WorkspacePath ResolveRelative(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains('\\') ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException(
                $"Whole-skin {role} is not a canonical lab-relative path.");
        string full = Path.GetFullPath(Path.Combine(
            labRoot.Value,
            value.Replace('/', Path.DirectorySeparatorChar)));
        var result = new WorkspacePath(full);
        if (!result.IsUnder(labRoot) || result == labRoot)
            throw new InvalidDataException(
                $"Whole-skin {role} escaped the K-local lab root.");
        return result;
    }

    private static AssetPath TexturePath(JsonElement element, string name)
    {
        var path = new AssetPath(RequiredString(element, name));
        if (!path.Value.StartsWith("textures/",
                StringComparison.OrdinalIgnoreCase) ||
            !path.Value.EndsWith(".dds",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Whole-skin {name} is not a canonical textures/*.dds path.");
        return path;
    }

    private static string SemanticKey(SkyrimNpcWholeSkinAuthority authority)
    {
        var builder = new StringBuilder();
        builder.AppendLine(authority.Route.ToString());
        builder.AppendLine(authority.Race.ToString());
        AppendBinding(builder, authority.RaceBinding);
        AppendBinding(builder, authority.SkinArmor);
        foreach (SkyrimNpcSkinRegionAuthority region in authority.Regions)
        {
            builder.Append(region.Region).Append('|')
                .Append(region.SlotMask).AppendLine();
            AppendBinding(builder, region.ArmorAddon);
            builder.AppendLine(region.TextureRoute.ToString());
            if (region.TextureSet is null)
            {
                builder.AppendLine("no-txst");
            }
            else
            {
                AppendBinding(builder, region.TextureSet);
            }
            if (region.Textures is null)
            {
                builder.AppendLine("no-texture-paths");
            }
            else
            {
                builder.Append(region.Textures.Diffuse.Value).Append('|')
                    .Append(region.Textures.Normal.Value).Append('|')
                    .Append(region.Textures.Subsurface.Value).Append('|')
                    .Append(region.Textures.Specular.Value).AppendLine();
            }
            foreach (SkyrimAssetAuthority asset in region.TextureAssets)
            {
                builder.Append(asset.ProviderId).Append('|')
                    .Append(asset.ProviderKind).Append('|')
                    .Append(asset.ProviderPath.Value).Append('|')
                    .Append(asset.ProviderSha256.Value).Append('|')
                    .Append(asset.AssetPath.Value).Append('|')
                    .Append(asset.ContentLength).Append('|')
                    .Append(asset.ContentSha256.Value).AppendLine();
            }
        }
        if (authority.ExposedOutfitSkinBinding is { } outfitBinding)
        {
            builder.AppendLine("exposed-outfit");
            AppendBinding(builder, outfitBinding.Outfit);
            AppendBinding(builder, outfitBinding.Armor);
            AppendBinding(builder, outfitBinding.ArmorAddon);
            AppendBinding(builder,
                outfitBinding.TargetFemaleSkinTextureSet);
            builder.Append(outfitBinding.ExposedSlotMask).AppendLine();
        }
        else
        {
            builder.AppendLine("no-exposed-outfit");
        }
        builder.Append(authority.RuntimeAuthority);
        return builder.ToString();
    }

    private static void AppendBinding(
        StringBuilder builder,
        RaceMenuNpcFormBinding binding) =>
        builder.Append(binding.Signature.Value).Append('|')
            .Append(binding.SourceReference).Append('|')
            .Append(binding.Reference).Append('|')
            .Append(binding.ProviderPluginName.Value).Append('|')
            .Append(binding.ProviderPlugin.Value).Append('|')
            .Append(binding.ProviderPluginSha256.Value).AppendLine();

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new InvalidDataException(
                        $"JSON property '{property.Name}' is duplicated.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                RejectDuplicateKeys(item);
        }
    }

    private static void RequireShape(
        JsonElement element,
        string role,
        params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{role} must be an object.");
        HashSet<string> expected = names.ToHashSet(StringComparer.Ordinal);
        string? unknown = element.EnumerateObject()
            .Select(item => item.Name)
            .FirstOrDefault(name => !expected.Contains(name));
        if (unknown is not null)
            throw new InvalidDataException(
                $"{role} contains unsupported property '{unknown}'.");
        string? missing = names.FirstOrDefault(name =>
            !element.TryGetProperty(name, out _));
        if (missing is not null)
            throw new InvalidDataException(
                $"{role} is missing required property '{missing}'.");
    }

    private static string RequiredString(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        string? result = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException(
                $"Property '{name}' must be a non-empty string.");
        return result;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
            throw new InvalidDataException(
                $"Property '{name}' must be an Int32.");
        return result;
    }

    private static long RequiredLong(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result))
            throw new InvalidDataException(
                $"Property '{name}' must be an Int64.");
        return result;
    }

    private static bool RequiredBool(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"Property '{name}' must be Boolean.")
        };
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuNpcWholeSkinAuthorityReadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record ParsedAuthority(
        WorkspacePath DataRoot,
        NpcSex Sex,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder,
        SkyrimNpcWholeSkinAuthority Snapshot);
}
