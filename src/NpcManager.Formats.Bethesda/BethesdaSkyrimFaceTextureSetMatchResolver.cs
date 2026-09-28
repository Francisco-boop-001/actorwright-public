using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reopens one reviewed plugin order and resolves a unique winning TXST from
/// the direct shader-slot paths embedded in a RaceMenu JSlot.
/// </summary>
public sealed class BethesdaSkyrimFaceTextureSetMatchResolver(
    ISkyrimFaceRecordPluginAuthorityLoader authorityLoader)
    : ISkyrimFaceTextureSetMatchResolver
{
    private const int MaximumPlugins = 64;
    private static readonly AssetPath BlankDetailMap =
        new("Actors/Character/Male/BlankDetailmap.dds");

    public async ValueTask<SkyrimFaceTextureSetMatchResult> ResolveAsync(
        SkyrimFaceTextureSetMatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Dictionary<int, AssetPath>? required = Validate(request, diagnostics);
        if (required is null || HasErrors(diagnostics)) return Refused(diagnostics);

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
                diagnostics.Add(Error("skyrim-face-texture-match-stale",
                    "The copied plugin order no longer matches the reviewed paths and SHA-256 values."));
            }
            return Refused(diagnostics);
        }

        var winners = new Dictionary<FormKey, DecodedTextureSet>();
        try
        {
            foreach (SkyrimFaceRecordPluginAuthority plugin in current.Authorities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var mod = SkyrimMod.CreateFromBinaryOverlay(
                    plugin.Path.Value, SkyrimRelease.SkyrimSE);
                foreach (ITextureSetGetter textureSet in mod.TextureSets)
                {
                    winners[textureSet.FormKey] = new DecodedTextureSet(
                        textureSet.FormKey,
                        new SkyrimFaceRecordProvider(
                            plugin.Plugin, plugin.Path, plugin.ExpectedSha256),
                        textureSet.Diffuse,
                        textureSet.NormalOrGloss,
                        textureSet.GlowOrDetailMap,
                        textureSet.Height,
                        textureSet.EnvironmentMaskOrSubsurfaceTint,
                        textureSet.Environment,
                        textureSet.Multilayer,
                        textureSet.BacklightMaskOrSpecular,
                        textureSet.IsDeleted);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("skyrim-face-texture-match-provider-malformed",
                $"The reviewed plugin order could not be decoded as Skyrim SE TXST records: {exception.Message}"));
            return Refused(diagnostics);
        }

        DecodedTextureSet[] matches;
        try
        {
            matches = winners.Values
                .Where(item => !item.IsDeleted && Matches(item, required))
                .ToArray();
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("skyrim-face-texture-match-path", exception.Message));
            return Refused(diagnostics);
        }
        if (matches.Length == 0)
        {
            diagnostics.Add(Error("skyrim-face-texture-match-count",
                "Direct RaceMenu faceTextures did not resolve any winning complete TXST."));
            return Refused(diagnostics);
        }
        DecodedTextureSet winner;
        if (request.PreferredTextureSet is { } preferred)
        {
            DecodedTextureSet[] preferredMatches = matches.Where(item =>
                    string.Equals(item.FormKey.ModKey.FileName.String,
                        preferred.Plugin.Value, StringComparison.OrdinalIgnoreCase) &&
                    item.FormKey.ID == preferred.FormId.Value)
                .ToArray();
            if (preferredMatches.Length != 1)
            {
                diagnostics.Add(Error("skyrim-face-texture-match-preferred",
                    $"Explicit headTexture {preferred} does not match every direct JSlot faceTextures slot."));
                return Refused(diagnostics);
            }
            winner = preferredMatches[0];
        }
        else
        {
            winner = matches
                .OrderBy(item => Array.FindIndex(current.Authorities.ToArray(),
                    authority => string.Equals(authority.Plugin.Value,
                        item.FormKey.ModKey.FileName.String,
                        StringComparison.OrdinalIgnoreCase)))
                .ThenBy(item => item.FormKey.ID)
                .First();
        }
        try
        {
            var textureSet = new FormReference(
                new PluginName(winner.FormKey.ModKey.FileName.String),
                new FormId(winner.FormKey.ID));
            AssetPath height = required.TryGetValue(3, out AssetPath declaredHeight)
                ? declaredHeight
                : BlankDetailMap;
            AssetPath? environmentMask =
                required.TryGetValue(4, out AssetPath declaredEnvironmentMask)
                    ? declaredEnvironmentMask
                    : null;
            AssetPath? environment =
                required.TryGetValue(5, out AssetPath declaredEnvironment)
                    ? declaredEnvironment
                    : null;
            AssetPath? multilayer =
                required.TryGetValue(6, out AssetPath declaredMultilayer)
                    ? declaredMultilayer
                    : null;
            AssetPath backlight = ResolveBacklightSlot(
                required,
                winner,
                matches,
                request.PreferredTextureSet is not null,
                diagnostics);
            if (HasErrors(diagnostics)) return Refused(diagnostics);
            var paths = new SkyrimPrivateHeadTexturePaths(
                required[0],
                required[1],
                required[2],
                height,
                backlight,
                environmentMask,
                environment,
                multilayer);
            diagnostics.Add(new Diagnostic(
                "skyrim-face-texture-match-resolved",
                DiagnosticSeverity.Info,
                matches.Length == 1
                    ? $"Direct RaceMenu faceTextures resolved typed TXST representative {textureSet}."
                    : $"Direct RaceMenu faceTextures matched {matches.Length} winning TXST records on every declared slot; deterministic representative {textureSet} supplies typed TXST identity while the JSlot paths own the output texture slots."));
            return new SkyrimFaceTextureSetMatchResult(
                true,
                new SkyrimFaceTextureSetAuthority(
                    textureSet, winner.Provider, paths, RuntimeAuthority: false),
                diagnostics.ToImmutable());
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("skyrim-face-texture-match-path", exception.Message));
            return Refused(diagnostics);
        }
    }

    private static AssetPath ResolveBacklightSlot(
        Dictionary<int, AssetPath> required,
        DecodedTextureSet winner,
        DecodedTextureSet[] matches,
        bool hasPreferredTextureSet,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (required.TryGetValue(7, out AssetPath declared))
            return declared;

        AssetPath? inferred = OptionalTexture(winner.BacklightMaskOrSpecular);
        if (inferred is null)
        {
            if (!hasPreferredTextureSet)
            {
                string[] distinct = matches
                    .Select(item => OptionalTexture(item.BacklightMaskOrSpecular)?.Value ?? string.Empty)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (distinct.Length != 1 || !string.IsNullOrWhiteSpace(distinct[0]))
                {
                    diagnostics.Add(Error("skyrim-face-texture-match-source-path",
                        "Direct RaceMenu faceTextures omitted shader slot 7 and matching TXST records do not agree on one inherited backlight/specular texture."));
                    return BlankDetailMap;
                }
            }

            diagnostics.Add(new Diagnostic(
                "skyrim-face-texture-match-blank-slot7",
                DiagnosticSeverity.Info,
                "Direct RaceMenu faceTextures and the matched TXST omitted shader slot 7; the private TXST will use the blank detail map as a static fallback."));
            return BlankDetailMap;
        }

        if (!hasPreferredTextureSet)
        {
            string[] distinct = matches
                .Select(item => OptionalTexture(item.BacklightMaskOrSpecular)?.Value ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinct.Length != 1 || string.IsNullOrWhiteSpace(distinct[0]))
            {
                diagnostics.Add(Error("skyrim-face-texture-match-source-path",
                    "Direct RaceMenu faceTextures omitted shader slot 7 and matching TXST records do not agree on one inherited backlight/specular texture."));
                return inferred.Value;
            }
        }

        diagnostics.Add(new Diagnostic(
            "skyrim-face-texture-match-inferred-slot7",
            DiagnosticSeverity.Info,
            "Direct RaceMenu faceTextures omitted shader slot 7; the matched TXST supplied the inherited backlight/specular texture."));
        return inferred.Value;
    }

    private static Dictionary<int, AssetPath>? Validate(
        SkyrimFaceTextureSetMatchRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("skyrim-face-texture-match-edition",
                "Direct faceTextures matching supports Skyrim Special Edition only."));
        if (string.IsNullOrWhiteSpace(request.DataRoot.Value))
            diagnostics.Add(Error("skyrim-face-texture-match-data-root",
                "A reviewed copied Data root is required."));
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Length > MaximumPlugins ||
            request.PluginOrder.Any(item => item is null) ||
            request.PluginOrder.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.PluginOrder.Length ||
            request.PluginOrder.Select(item => item.Path.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.PluginOrder.Length)
        {
            diagnostics.Add(Error("skyrim-face-texture-match-plugin-order",
                $"PluginOrder must contain 1-{MaximumPlugins} distinct reviewed authorities."));
        }
        if (request.FaceTextures.IsDefaultOrEmpty ||
            request.FaceTextures.Length > 8 ||
            request.FaceTextures.Select(item => item.Index).Distinct().Count() !=
            request.FaceTextures.Length ||
            request.FaceTextures.Any(item => item.Index is < 0 or > 7))
        {
            diagnostics.Add(Error("skyrim-face-texture-match-source",
                "Direct RaceMenu faceTextures must contain 1-8 distinct shader slots in range 0-7."));
            return null;
        }

        var required = new Dictionary<int, AssetPath>();
        try
        {
            foreach (RaceMenuFaceTexture row in request.FaceTextures)
                required.Add(row.Index, NormalizeTexture(row.Texture));
            if (!required.Keys.ToHashSet().IsSupersetOf([0, 1, 2]))
                throw new ArgumentException(
                    "Direct RaceMenu faceTextures must declare shader slots 0, 1, and 2.");
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("skyrim-face-texture-match-source-path",
                exception.Message));
            return null;
        }
        return required;
    }

    private static bool Matches(
        DecodedTextureSet candidate,
        IReadOnlyDictionary<int, AssetPath> required)
    {
        string?[] slots =
        [
            candidate.Diffuse,
            candidate.NormalOrGloss,
            candidate.GlowOrDetailMap,
            candidate.Height,
            candidate.EnvironmentMaskOrSubsurfaceTint,
            candidate.Environment,
            candidate.Multilayer,
            candidate.BacklightMaskOrSpecular
        ];
        foreach ((int index, AssetPath expected) in required)
        {
            AssetPath? actual = OptionalTexture(slots[index]);
            if (actual is null ||
                !string.Equals(actual.Value.Value, expected.Value,
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static AssetPath? OptionalTexture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return NormalizeTexture(value);
    }

    private static AssetPath NormalizeTexture(string value)
    {
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["textures/".Length..];
        if (!normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Face texture paths must identify DDS assets.");
        return new AssetPath(normalized);
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

    private static SkyrimFaceTextureSetMatchResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record DecodedTextureSet(
        FormKey FormKey,
        SkyrimFaceRecordProvider Provider,
        string? Diffuse,
        string? NormalOrGloss,
        string? GlowOrDetailMap,
        string? Height,
        string? EnvironmentMaskOrSubsurfaceTint,
        string? Environment,
        string? Multilayer,
        string? BacklightMaskOrSpecular,
        bool IsDeleted);
}
