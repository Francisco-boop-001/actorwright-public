using System.Collections.Immutable;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reopens a reviewed copied-plugin order and exposes only the winning RACE
/// fields required to map RaceMenu tint rows into a new NPC record.
/// </summary>
public sealed class BethesdaSkyrimRaceTintAuthorityReader(
    ISkyrimFaceRecordPluginAuthorityLoader authorityLoader)
    : ISkyrimRaceTintAuthorityReader
{
    private const int MaximumPlugins = 64;
    // Vanilla Nord female head data contains 33 rows. Keep a defensive bound
    // without rejecting ordinary engine-authored RACE tables.
    private const int MaximumRaceLayers = 256;

    public async ValueTask<SkyrimRaceTintAuthorityResult> ReadAsync(
        SkyrimRaceTintAuthorityRequest request,
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
                diagnostics.Add(Error("skyrim-race-tint-authority-stale",
                    "The copied plugin order no longer matches the reviewed paths and SHA-256 values."));
            }
            return Refused(diagnostics);
        }

        SkyrimNativeTintCatalog catalog;
        try
        {
            catalog = BethesdaSkyrimNativeFaceTintCatalogLoader.Load(
                current.Authorities, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("skyrim-race-tint-provider-malformed",
                $"The copied plugin order could not be decoded as Skyrim SE tint records: {exception.Message}"));
            return Refused(diagnostics);
        }

        if (!catalog.Races.TryGetValue(
                SkyrimNativeTintRecordKey.From(request.Race), out SkyrimNativeTintRace? race) ||
            race.IsDeleted)
        {
            diagnostics.Add(Error("skyrim-race-tint-race-missing",
                $"Winning RACE {request.Race} is unavailable in the reviewed plugin order."));
            return Refused(diagnostics);
        }

        ImmutableArray<SkyrimNativeTintRaceLayer> sourceLayers =
            request.Sex == NpcSex.Female ? race.FemaleLayers : race.MaleLayers;
        if (sourceLayers.Length > MaximumRaceLayers)
        {
            diagnostics.Add(Error("skyrim-race-tint-layer-limit",
                $"RACE {request.Race} exposes {sourceLayers.Length} tint layers; the engine contract admits at most {MaximumRaceLayers}."));
            return Refused(diagnostics);
        }

        var layers = ImmutableArray.CreateBuilder<SkyrimRaceTintLayerAuthority>(
            sourceLayers.Length);
        for (var order = 0; order < sourceLayers.Length; order++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SkyrimNativeTintRaceLayer source = sourceLayers[order];
            if (source.Index is not { } index)
            {
                diagnostics.Add(Error("skyrim-race-tint-index",
                    $"RACE {request.Race} tint row {order} has no TINI index."));
                continue;
            }

            try
            {
                AssetPath maskPath = ToTexturePath(source.MaskPath);
                var kind = source.MaskType == Convert.ToInt32(TintAssets.TintMaskType.SkinTone)
                    ? SkyrimRaceTintMaskKind.SkinTone
                    : SkyrimRaceTintMaskKind.Other;
                layers.Add(new SkyrimRaceTintLayerAuthority(
                    order, index, source.MaskType, kind, maskPath));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error("skyrim-race-tint-mask-path",
                    $"RACE {request.Race} tint index {index} has an invalid mask path: {exception.Message}"));
            }
        }

        if (HasErrors(diagnostics)) return Refused(diagnostics);
        int duplicateIndexCount = layers
            .GroupBy(item => item.Index)
            .Count(group => group.Count() > 1);
        if (duplicateIndexCount > 0)
        {
            diagnostics.Add(Warning("skyrim-race-tint-index-reused",
                $"RACE {request.Race} reuses {duplicateIndexCount} TINI value(s) across distinct tint rows. " +
                "The exact rows remain available for path matching, but a reused TINI may not be authored on an NPC."));
        }
        return new SkyrimRaceTintAuthorityResult(
            true,
            new SkyrimRaceTintAuthority(
                race.Reference,
                race.Provider,
                request.Sex,
                layers.ToImmutable(),
                RuntimeAuthority: false),
            diagnostics.ToImmutable());
    }

    private static void ValidateRequest(
        SkyrimRaceTintAuthorityRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("skyrim-race-tint-edition",
                "RACE tint authority supports Skyrim Special Edition only."));
        }
        if (!Enum.IsDefined(request.Sex))
        {
            diagnostics.Add(Error("skyrim-race-tint-sex",
                "The requested NPC sex is unsupported."));
        }
        if (string.IsNullOrWhiteSpace(request.DataRoot.Value))
        {
            diagnostics.Add(Error("skyrim-race-tint-data-root",
                "A reviewed copied Data root is required."));
        }
        if (string.IsNullOrWhiteSpace(request.Race.Plugin.Value) ||
            request.Race.FormId.Value is 0 or > 0x00FF_FFFF)
        {
            diagnostics.Add(Error("skyrim-race-tint-race",
                "The selected RACE must be an explicit nonzero plugin-local reference."));
        }
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Length > MaximumPlugins ||
            request.PluginOrder.Any(item => item is null))
        {
            diagnostics.Add(Error("skyrim-race-tint-plugin-order",
                $"PluginOrder must contain 1-{MaximumPlugins} explicit reviewed authorities."));
            return;
        }
        if (request.PluginOrder.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length ||
            request.PluginOrder.Select(item => item.Path.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
        {
            diagnostics.Add(Error("skyrim-race-tint-plugin-order-duplicate",
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
                    reviewed[index].Plugin.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current[index].Path.Value,
                    reviewed[index].Path.Value, StringComparison.OrdinalIgnoreCase) ||
                current[index].ExpectedSha256 != reviewed[index].ExpectedSha256)
            {
                return false;
            }
        }
        return true;
    }

    private static AssetPath ToTexturePath(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            normalized = "textures/" + normalized;
        if (!normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Skyrim tint masks must be DDS assets.");
        return new AssetPath(normalized);
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Diagnostic Warning(string code, string message) =>
        new(code, DiagnosticSeverity.Warning, message);

    private static SkyrimRaceTintAuthorityResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
