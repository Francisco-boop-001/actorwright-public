using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Reads a winning Skyrim TXST only from a reopened reviewed order.</summary>
public sealed class BethesdaSkyrimFaceTextureSetAuthorityReader(
    ISkyrimFaceRecordPluginAuthorityLoader authorityLoader)
    : ISkyrimFaceTextureSetAuthorityReader
{
    private const int MaximumPlugins = 64;

    public async ValueTask<SkyrimFaceTextureSetAuthorityResult> ReadAsync(
        SkyrimFaceTextureSetAuthorityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimFaceRecordPluginAuthorityResult current =
            await authorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    request.Edition,
                    request.DataRoot,
                    request.PluginOrder.Select(item => item.Plugin).ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(current.Diagnostics);
        if (!current.Accepted ||
            !MatchesReviewedAuthorities(current.Authorities, request.PluginOrder))
        {
            if (current.Accepted)
            {
                diagnostics.Add(Error("skyrim-face-texture-authority-stale",
                    "The copied plugin order no longer matches the reviewed paths and SHA-256 values."));
            }
            return Refused(diagnostics);
        }

        var key = new FormKey(
            ModKey.FromNameAndExtension(request.TextureSet.Plugin.Value),
            request.TextureSet.FormId.Value);
        DecodedTextureSet? winner = null;
        try
        {
            foreach (SkyrimFaceRecordPluginAuthority plugin in current.Authorities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var mod = SkyrimMod.CreateFromBinaryOverlay(
                    plugin.Path.Value, SkyrimRelease.SkyrimSE);
                ITextureSetGetter? candidate = mod.TextureSets
                    .FirstOrDefault(item => item.FormKey == key);
                if (candidate is null) continue;
                winner = new DecodedTextureSet(
                    new SkyrimFaceRecordProvider(
                        plugin.Plugin, plugin.Path, plugin.ExpectedSha256),
                    candidate.Diffuse,
                    candidate.NormalOrGloss,
                    candidate.GlowOrDetailMap,
                    candidate.Height,
                    candidate.BacklightMaskOrSpecular,
                    candidate.EnvironmentMaskOrSubsurfaceTint,
                    candidate.Environment,
                    candidate.Multilayer,
                    candidate.IsDeleted);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("skyrim-face-texture-provider-malformed",
                $"The copied plugin order could not be decoded as Skyrim SE TXST records: {exception.Message}"));
            return Refused(diagnostics);
        }

        if (winner is null || winner.IsDeleted)
        {
            diagnostics.Add(Error("skyrim-face-texture-record-missing",
                $"Winning TXST {request.TextureSet} is unavailable in the reviewed plugin order."));
            return Refused(diagnostics);
        }

        try
        {
            var paths = new SkyrimPrivateHeadTexturePaths(
                RequiredTexture(winner.Diffuse, "diffuse"),
                RequiredTexture(winner.NormalOrGloss, "normal/gloss"),
                RequiredTexture(winner.GlowOrDetailMap, "glow/detail"),
                RequiredTexture(winner.Height, "height/detail"),
                RequiredTexture(winner.BacklightMaskOrSpecular, "backlight/specular"),
                OptionalTexture(winner.EnvironmentMaskOrSubsurfaceTint),
                OptionalTexture(winner.Environment),
                OptionalTexture(winner.Multilayer));
            return new SkyrimFaceTextureSetAuthorityResult(
                true,
                new SkyrimFaceTextureSetAuthority(
                    request.TextureSet,
                    winner.Provider,
                    paths,
                    RuntimeAuthority: false),
                diagnostics.ToImmutable());
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("skyrim-face-texture-path",
                $"TXST {request.TextureSet} has an invalid required face texture: {exception.Message}"));
            return Refused(diagnostics);
        }
    }

    private static AssetPath RequiredTexture(string? value, string role)
    {
        AssetPath? result = OptionalTexture(value);
        return result ?? throw new ArgumentException($"The {role} texture path is absent.");
    }

    private static AssetPath? OptionalTexture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["textures/".Length..];
        if (!normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Face texture paths must identify DDS assets.");
        return new AssetPath(normalized);
    }

    private static void ValidateRequest(
        SkyrimFaceTextureSetAuthorityRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("skyrim-face-texture-edition",
                "Face TXST authority supports Skyrim Special Edition only."));
        if (string.IsNullOrWhiteSpace(request.DataRoot.Value))
            diagnostics.Add(Error("skyrim-face-texture-data-root",
                "A reviewed copied Data root is required."));
        if (string.IsNullOrWhiteSpace(request.TextureSet.Plugin.Value) ||
            request.TextureSet.FormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("skyrim-face-texture-reference",
                "The TXST must be an explicit nonzero plugin-local reference."));
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Length > MaximumPlugins ||
            request.PluginOrder.Any(item => item is null))
        {
            diagnostics.Add(Error("skyrim-face-texture-plugin-order",
                $"PluginOrder must contain 1-{MaximumPlugins} explicit reviewed authorities."));
            return;
        }
        if (request.PluginOrder.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length ||
            request.PluginOrder.Select(item => item.Path.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
            diagnostics.Add(Error("skyrim-face-texture-plugin-order-duplicate",
                "PluginOrder may not repeat a plugin identity or provider path."));
    }

    private static bool MatchesReviewedAuthorities(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> current,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> reviewed)
    {
        if (current.Length != reviewed.Length) return false;
        for (var index = 0; index < current.Length; index++)
        {
            if (!string.Equals(current[index].Plugin.Value,
                    reviewed[index].Plugin.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current[index].Path.Value,
                    reviewed[index].Path.Value, StringComparison.OrdinalIgnoreCase) ||
                current[index].ExpectedSha256 != reviewed[index].ExpectedSha256)
                return false;
        }
        return true;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimFaceTextureSetAuthorityResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record DecodedTextureSet(
        SkyrimFaceRecordProvider Provider,
        string? Diffuse,
        string? NormalOrGloss,
        string? GlowOrDetailMap,
        string? Height,
        string? BacklightMaskOrSpecular,
        string? EnvironmentMaskOrSubsurfaceTint,
        string? Environment,
        string? Multilayer,
        bool IsDeleted);
}
