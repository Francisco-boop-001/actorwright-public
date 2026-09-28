using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Canonical hashes shared by reviewed intake and downstream read-only
/// consumers. A consumer may recompute this inventory, but may not substitute
/// a different provider-ordering or winner policy.
/// </summary>
public static class AssetProviderInventoryAuthority
{
    public static Sha256Hash Fingerprint(
        ImmutableArray<AssetProvider> providers)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (AssetProvider provider in providers
                     .OrderBy(
                         item => item.Path.Value,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Kind)
                     .ThenBy(
                         item => item.Source,
                         StringComparer.OrdinalIgnoreCase))
        {
            Append(hash, provider.Path.Value);
            Append(hash, provider.Kind.ToString());
            Append(hash, provider.Source);
            Append(
                hash,
                provider.Size.ToString(
                    CultureInfo.InvariantCulture));
            Append(hash, provider.Sha256);
        }
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    private static void Append(
        IncrementalHash hash,
        string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }
}

public static class ReviewedGameIntakeFingerprintAuthority
{
    public static Sha256Hash Fingerprint(
        GameEdition edition,
        WorkspacePath workspaceRoot,
        WorkspacePath dataRoot,
        WorkspacePath loadOrderPath,
        WorkspacePath outputRoot,
        Sha256Hash loadOrderHash,
        ImmutableArray<PluginClosureReviewEntry> plugins,
        ImmutableArray<ReviewedBodySidecar> bodySidecars,
        ImmutableArray<GeneratedPluginScanEntry>
            generatedPlugins,
        ImmutableArray<GeneratedSidecarEntry>
            generatedSidecars,
        Sha256Hash assetIndexFingerprint)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        Append(hash, edition.ToWireName());
        Append(hash, workspaceRoot.Value);
        Append(hash, dataRoot.Value);
        Append(hash, loadOrderPath.Value);
        Append(hash, outputRoot.Value);
        Append(hash, loadOrderHash.Value);
        foreach (PluginClosureReviewEntry plugin in plugins)
        {
            Append(hash, plugin.Plugin.Value);
            Append(
                hash,
                plugin.Order.ToString(
                    CultureInfo.InvariantCulture));
            Append(
                hash,
                plugin.SourceHash?.Value ??
                string.Empty);
            foreach (PluginName master in plugin.Masters)
                Append(hash, master.Value);
        }
        foreach (ReviewedBodySidecar sidecar in
                 bodySidecars.OrderBy(
                     item => item.Plugin.Value,
                     StringComparer.OrdinalIgnoreCase))
        {
            Append(hash, sidecar.Plugin.Value);
            Append(hash, sidecar.SourceHash.Value);
            Append(hash, sidecar.CanonicalHash.Value);
        }
        foreach (GeneratedPluginScanEntry plugin in
                 generatedPlugins)
        {
            Append(hash, plugin.Plugin.Value);
            Append(
                hash,
                plugin.Sha256?.Value ??
                string.Empty);
        }
        foreach (GeneratedSidecarEntry sidecar in
                 generatedSidecars)
        {
            Append(hash, sidecar.RelativePath.Value);
            Append(hash, sidecar.Sha256.Value);
        }
        Append(hash, assetIndexFingerprint.Value);
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    public static Sha256Hash Fingerprint(
        ReviewedGameIntake intake) =>
        Fingerprint(
            intake.Edition,
            intake.WorkspaceRoot,
            intake.DataRoot,
            intake.LoadOrderPath,
            intake.OutputRoot,
            intake.LoadOrderHash,
            intake.Plugins,
            intake.BodySidecars,
            intake.GeneratedPlugins,
            intake.GeneratedSidecars,
            intake.AssetIndexFingerprint);

    private static void Append(
        IncrementalHash hash,
        string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }
}

public sealed record FaceGeomHairTextureAuthority(
    AssetPath AssetPath,
    AssetProviderKind ProviderKind,
    string Provider,
    Sha256Hash Sha256,
    long Bytes,
    WorkspacePath MaterializedPath);

public static class FaceGeomHairTextureAuthorityCanonical
{
    public static ImmutableArray<FaceGeomHairTextureAuthority>
        Order(
            IEnumerable<FaceGeomHairTextureAuthority> textures) =>
        textures
            .OrderBy(
                item => item.AssetPath.Value,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                item => item.AssetPath.Value,
                StringComparer.Ordinal)
            .ToImmutableArray();

    public static Sha256Hash Fingerprint(
        ImmutableArray<FaceGeomHairTextureAuthority> textures)
    {
        ImmutableArray<FaceGeomHairTextureAuthority> ordered =
            Order(textures);
        if (!textures.SequenceEqual(ordered))
            throw new ArgumentException(
                "Texture authority rows must be in canonical asset-path order.",
                nameof(textures));
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (FaceGeomHairTextureAuthority texture in textures)
        {
            string row =
                $"{texture.AssetPath.Value}\0" +
                $"{texture.ProviderKind}\0" +
                $"{texture.Provider}\0" +
                $"{texture.Sha256.Value}\0" +
                $"{texture.Bytes}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(row));
        }
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }
}
