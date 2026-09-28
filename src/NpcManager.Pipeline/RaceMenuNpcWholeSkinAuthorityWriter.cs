using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Serializes one resolved whole-skin snapshot to a fresh K-local manifest and
/// reopens the exact bytes. It never writes a plugin or game asset.
/// </summary>
public sealed class RaceMenuNpcWholeSkinAuthorityWriter(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot)
    : IRaceMenuNpcWholeSkinAuthorityWriter
{
    private const int MaximumManifestBytes = 2 * 1024 * 1024;

    public async ValueTask<RaceMenuNpcWholeSkinAuthorityWriteResult> WriteAsync(
        RaceMenuNpcWholeSkinAuthorityWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Validate(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            bytes = Serialize(request);
            if (bytes.Length is <= 0 or > MaximumManifestBytes)
            {
                diagnostics.Add(Error("racemenu-whole-skin-authority-size",
                    $"Whole-skin authority must be 1-{MaximumManifestBytes} bytes."));
                return Refused(diagnostics);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           ArgumentException or
                                           InvalidOperationException)
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-serialize",
                exception.Message));
            return Refused(diagnostics);
        }

        try
        {
            await using (var stream = new FileStream(
                request.Destination.Value,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            byte[] reopened = await File.ReadAllBytesAsync(
                request.Destination.Value, cancellationToken).ConfigureAwait(false);
            var hash = new Sha256Hash(
                Convert.ToHexString(SHA256.HashData(reopened)));
            if (!reopened.AsSpan().SequenceEqual(bytes))
            {
                diagnostics.Add(Error("racemenu-whole-skin-authority-readback",
                    "Written whole-skin authority did not reopen byte-for-byte."));
                TryDelete(request.Destination);
                return Refused(diagnostics);
            }
            using JsonDocument document = JsonDocument.Parse(reopened);
            int reopenedSchema =
                document.RootElement.GetProperty("schemaVersion").GetInt32();
            if (reopenedSchema is not (2 or 3))
            {
                diagnostics.Add(Error("racemenu-whole-skin-authority-readback",
                    "Written whole-skin authority lost its schema identity."));
                TryDelete(request.Destination);
                return Refused(diagnostics);
            }

            return new RaceMenuNpcWholeSkinAuthorityWriteResult(
                true,
                new RaceMenuNpcWholeSkinAuthorityArtifact(
                    new RaceMenuNpcWholeSkinAuthority(
                        request.Destination, hash),
                    request.Snapshot),
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDelete(request.Destination);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           JsonException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            TryDelete(request.Destination);
            diagnostics.Add(Error("racemenu-whole-skin-authority-write",
                exception.Message));
            return Refused(diagnostics);
        }
    }

    private byte[] Serialize(RaceMenuNpcWholeSkinAuthorityWriteRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            SkipValidation = false
        }))
        {
            SkyrimNpcWholeSkinAuthority snapshot = request.Snapshot;
            bool hasMeshEmbeddedRegions = snapshot.Regions
                .Any(item => item.TextureSet is null);
            int schemaVersion = hasMeshEmbeddedRegions ? 3 : 2;
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WriteString("edition", "skyrimse");
            writer.WriteString("dataRoot", Relative(request.DataRoot));
            writer.WriteString("race", snapshot.Race.ToString());
            writer.WriteString("sex", "female");
            writer.WriteString("route", "inherited-race");
            writer.WriteBoolean("runtimeAuthority", false);
            writer.WriteStartArray("pluginOrder");
            foreach (SkyrimFaceRecordPluginAuthority plugin in request.PluginOrder)
            {
                writer.WriteStartObject();
                writer.WriteString("plugin", plugin.Plugin.Value);
                writer.WriteString("path", Relative(plugin.Path));
                writer.WriteString("sha256", plugin.ExpectedSha256.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("raceBinding");
            WriteBinding(writer, snapshot.RaceBinding);
            writer.WritePropertyName("skinArmor");
            WriteBinding(writer, snapshot.SkinArmor);
            writer.WriteStartArray("regions");
            foreach (SkyrimNpcSkinRegionAuthority region in snapshot.Regions)
            {
                writer.WriteStartObject();
                writer.WriteString("region", RegionName(region.Region));
                if (schemaVersion == 3)
                {
                    writer.WriteString(
                        "textureRoute",
                        region.TextureSet is null
                            ? "mesh-embedded"
                            : "txst");
                }
                writer.WriteNumber("slotMask", region.SlotMask);
                writer.WritePropertyName("armorAddon");
                WriteBinding(writer, region.ArmorAddon);
                writer.WritePropertyName("textureSet");
                if (region.TextureSet is null)
                    writer.WriteNullValue();
                else
                    WriteBinding(writer, region.TextureSet);
                writer.WritePropertyName("textures");
                if (region.Textures is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteString("diffuse", region.Textures.Diffuse.Value);
                    writer.WriteString("normal", region.Textures.Normal.Value);
                    writer.WriteString("subsurface", region.Textures.Subsurface.Value);
                    writer.WriteString("specular", region.Textures.Specular.Value);
                    writer.WriteEndObject();
                }
                writer.WriteStartArray("textureAssets");
                foreach (SkyrimAssetAuthority asset in region.TextureAssets)
                {
                    writer.WriteStartObject();
                    writer.WriteString("providerId", asset.ProviderId);
                    writer.WriteString("providerKind",
                        asset.ProviderKind == AssetProviderKind.Loose
                            ? "loose"
                            : "archive");
                    writer.WriteString("providerPath", Relative(asset.ProviderPath));
                    writer.WriteString("providerSha256", asset.ProviderSha256.Value);
                    writer.WriteString("assetPath", asset.AssetPath.Value);
                    writer.WriteNumber("contentLength", asset.ContentLength);
                    writer.WriteString("contentSha256", asset.ContentSha256.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("exposedOutfitSkinBinding");
            if (snapshot.ExposedOutfitSkinBinding is not { } outfitBinding)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("outfit");
                WriteBinding(writer, outfitBinding.Outfit);
                writer.WritePropertyName("armor");
                WriteBinding(writer, outfitBinding.Armor);
                writer.WritePropertyName("armorAddon");
                WriteBinding(writer, outfitBinding.ArmorAddon);
                writer.WritePropertyName("targetFemaleSkinTextureSet");
                WriteBinding(writer, outfitBinding.TargetFemaleSkinTextureSet);
                writer.WriteNumber("exposedSlotMask",
                    outfitBinding.ExposedSlotMask);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private void WriteBinding(
        Utf8JsonWriter writer,
        RaceMenuNpcFormBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteString("signature", binding.Signature.Value);
        writer.WriteString("sourceFormKey", binding.SourceReference.ToString());
        writer.WriteString("providerFormKey", binding.Reference.ToString());
        writer.WriteString("providerPluginName", binding.ProviderPluginName.Value);
        writer.WriteString("providerPluginPath", Relative(binding.ProviderPlugin));
        writer.WriteString("providerPluginSha256",
            binding.ProviderPluginSha256.Value);
        writer.WriteEndObject();
    }

    private string Relative(WorkspacePath path)
    {
        if (!path.IsUnder(labRoot) || path == labRoot)
            throw new InvalidDataException(
                "Whole-skin authority path escaped the K-local lab root.");
        return new AssetPath(Path.GetRelativePath(labRoot.Value, path.Value)
            .Replace(Path.DirectorySeparatorChar, '/')).Value;
    }

    private void Validate(
        RaceMenuNpcWholeSkinAuthorityWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(
            labRoot, request.DataRoot));
        diagnostics.AddRange(workspacePolicy.Evaluate(
            labRoot, request.Destination));
        SkyrimNpcWholeSkinAuthority snapshot = request.Snapshot;
        if (snapshot.Route != SkyrimNpcSkinRouteKind.InheritedRace ||
            snapshot.RuntimeAuthority ||
            snapshot.Regions.IsDefaultOrEmpty ||
            !snapshot.Regions.Select(item => item.Region)
                .SequenceEqual(new[]
                {
                    SkyrimNpcSkinRegion.Body,
                    SkyrimNpcSkinRegion.Hands,
                    SkyrimNpcSkinRegion.Feet
                }))
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-snapshot",
                "Only one complete static inherited-race body/hands/feet snapshot can be written."));
        }
        SkyrimNpcSkinRegionAuthority? bodyRegion =
            snapshot.Regions.IsDefault
                ? null
                : snapshot.Regions.FirstOrDefault(item =>
                    item.Region == SkyrimNpcSkinRegion.Body);
        if (snapshot.ExposedOutfitSkinBinding is { } outfitBinding &&
            (outfitBinding.Outfit.Signature != new RecordSignature("OTFT") ||
             outfitBinding.Armor.Signature != new RecordSignature("ARMO") ||
             outfitBinding.ArmorAddon.Signature != new RecordSignature("ARMA") ||
             outfitBinding.TargetFemaleSkinTextureSet.Signature !=
             new RecordSignature("TXST") ||
             outfitBinding.ExposedSlotMask != 0x04U ||
             bodyRegion is null ||
             bodyRegion.TextureSet is null ||
             outfitBinding.TargetFemaleSkinTextureSet.Reference !=
             bodyRegion.TextureSet.Reference))
        {
            diagnostics.Add(Error(
                "racemenu-whole-skin-authority-outfit-binding",
                "The exposed-outfit binding must be one torso OTFT/ARMO/ARMA route targeting the accepted body TXST."));
        }
        foreach (SkyrimNpcSkinRegionAuthority region in snapshot.Regions.IsDefault
                     ? ImmutableArray<SkyrimNpcSkinRegionAuthority>.Empty
                     : snapshot.Regions)
        {
            if (region.TextureSet is null)
            {
                if (region.Textures is not null ||
                    region.TextureAssets.IsDefaultOrEmpty ||
                    region.TextureAssets.Any(item =>
                        !item.AssetPath.Value.StartsWith(
                            "textures/",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    diagnostics.Add(Error(
                        "racemenu-whole-skin-authority-mesh-embedded-region",
                        "Mesh-embedded whole-skin regions require no TXST/textures object and one or more resolved textures/*.dds assets."));
                }
            }
            else if (region.Textures is null ||
                     region.TextureAssets.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error(
                    "racemenu-whole-skin-authority-txst-region",
                    "TXST whole-skin regions require a texture-set binding, texture paths, and resolved texture assets."));
            }
        }
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.PluginOrder.Length)
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-plugin-order",
                "Whole-skin plugin order must be explicit and duplicate-free."));
        }
        if (!request.DataRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-data-root",
                "Whole-skin Data root must be one existing K-local directory."));
        }
        if (!request.Destination.IsUnder(labRoot) ||
            request.Destination == labRoot ||
            !string.Equals(Path.GetExtension(request.Destination.Value), ".json",
                StringComparison.OrdinalIgnoreCase) ||
            File.Exists(request.Destination.Value) ||
            Directory.Exists(request.Destination.Value))
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-destination",
                "Destination must be one absent K-local .json file."));
        }
        string? parent = Path.GetDirectoryName(request.Destination.Value);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            diagnostics.Add(Error("racemenu-whole-skin-authority-parent",
                "Destination parent must already exist."));
        }
    }

    private static string RegionName(SkyrimNpcSkinRegion region) => region switch
    {
        SkyrimNpcSkinRegion.Body => "body",
        SkyrimNpcSkinRegion.Hands => "hands",
        SkyrimNpcSkinRegion.Feet => "feet",
        _ => throw new InvalidDataException("Unsupported skin region.")
    };

    private void TryDelete(WorkspacePath path)
    {
        try
        {
            if (path.IsUnder(labRoot) && File.Exists(path.Value))
                File.Delete(path.Value);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            // The original failure remains authoritative.
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuNpcWholeSkinAuthorityWriteResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
