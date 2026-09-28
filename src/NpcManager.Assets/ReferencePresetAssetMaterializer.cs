using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Assets;

/// <summary>
/// Adapts the existing strict loose/BSA content resolver to the reference
/// authoring Application port. It never performs winner discovery.
/// </summary>
public sealed class ReferencePresetAssetMaterializer(
    ISkyrimAssetContentResolver contentResolver)
    : IReferencePresetAssetMaterializer
{
    public async ValueTask<ReferencePresetAssetMaterializeResult> MaterializeAsync(
        ReferencePresetAssetMaterializeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorities = request.Authorities.Select(ToContentAuthority)
            .ToImmutableArray();
        SkyrimAssetContentResolutionResult result =
            await contentResolver.ResolveAsync(
                new SkyrimAssetContentResolutionRequest(
                    request.AllowedRoot, authorities),
                cancellationToken).ConfigureAwait(false);
        if (!result.Resolved)
            return new ReferencePresetAssetMaterializeResult([], result.Diagnostics);

        var plannedByPath = request.Authorities.ToDictionary(
            item => item.AssetPath.Value, StringComparer.OrdinalIgnoreCase);
        var assets = ImmutableArray.CreateBuilder<ReferencePresetAssetContent>(
            result.Assets.Length);
        foreach (ResolvedSkyrimAssetContent asset in result.Assets)
        {
            if (!plannedByPath.TryGetValue(asset.AssetPath.Value,
                    out SkyrimAssetAuthority? planned))
            {
                return new ReferencePresetAssetMaterializeResult(
                    [],
                    result.Diagnostics.Add(new Diagnostic(
                        "reference-asset-materializer-unplanned",
                        DiagnosticSeverity.Error,
                        $"Materializer returned unplanned asset '{asset.AssetPath}'.")));
            }
            assets.Add(new ReferencePresetAssetContent(planned, asset.Content));
        }
        if (assets.Count != request.Authorities.Length)
        {
            return new ReferencePresetAssetMaterializeResult(
                [],
                result.Diagnostics.Add(new Diagnostic(
                    "reference-asset-materializer-incomplete",
                    DiagnosticSeverity.Error,
                    "Materializer did not return every planned asset.")));
        }
        return new ReferencePresetAssetMaterializeResult(
            assets.ToImmutable(), result.Diagnostics);
    }

    private static SkyrimAssetContentAuthority ToContentAuthority(
        SkyrimAssetAuthority source) =>
        new(
            source.ProviderId,
            source.ProviderKind switch
            {
                AssetProviderKind.Loose => SkyrimAssetContentProviderKind.Loose,
                AssetProviderKind.Archive => SkyrimAssetContentProviderKind.Bsa,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(source), source.ProviderKind,
                    "Unsupported planned provider kind.")
            },
            source.ProviderPath,
            source.ProviderSha256,
            source.AssetPath,
            source.ContentLength,
            source.ContentSha256);
}
