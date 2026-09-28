using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Assets;

namespace NpcManager.Pipeline;

/// <summary>
/// Resolves all record and mask authorities before the FaceTint writer runs.
/// This keeps archive extraction, typed record semantics, and image composition
/// independently testable while preserving one all-or-nothing product command.
/// </summary>
public sealed class SkyrimNativeFaceTintMaterializationService(
    ISkyrimNativeFaceTintRecordResolver recordResolver,
    ISkyrimAssetContentResolver assetResolver,
    ISkyrimNativeFaceTintBuildService buildService)
    : ISkyrimNativeFaceTintMaterializationService
{
    public async ValueTask<SkyrimNativeFaceTintMaterializationResult> MaterializeAsync(
        SkyrimNativeFaceTintMaterializationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        SkyrimNativeFaceTintRecordResult records = await recordResolver.ResolveAsync(
            request.RecordRequest, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(records.Diagnostics);
        if (!records.Accepted || records.Route is null || HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }
        if (records.Route.Layers.IsDefault || records.Route.Layers.Any(item => item is null))
        {
            diagnostics.Add(Error("skyrim-native-tint-route-shape",
                "The accepted record route returned an invalid layer array."));
            return Refused(diagnostics);
        }
        if (request.MaskAuthorities.IsDefault ||
            request.MaskAuthorities.Any(item => item is null ||
                item.ProviderKind is not (AssetProviderKind.Loose or AssetProviderKind.Archive) ||
                string.IsNullOrWhiteSpace(item.AssetPath.Value)))
        {
            diagnostics.Add(Error("skyrim-native-tint-authority-shape",
                "Mask authorities must be explicit, present, and use loose or archive providers."));
            return Refused(diagnostics);
        }

        var activePaths = records.Route.Layers
            .Where(item => item.Coverage > 0F)
            .Select(item => item.MaskPath.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var authorityPaths = request.MaskAuthorities.Select(item => item.AssetPath.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (authorityPaths.Count != request.MaskAuthorities.Length ||
            !activePaths.SetEquals(authorityPaths))
        {
            diagnostics.Add(Error("skyrim-native-tint-authority-closure",
                "Mask authorities must exactly cover every active RACE-declared path once."));
            return Refused(diagnostics);
        }

        if (request.MaskAuthorities.IsEmpty)
        {
            SkyrimNativeFaceTintBuildResult emptyBuild = await buildService.BuildAsync(
                new SkyrimNativeFaceTintBuildRequest(
                    records.Route, [], request.OutputPath), cancellationToken)
                .ConfigureAwait(false);
            diagnostics.AddRange(emptyBuild.Diagnostics);
            return new SkyrimNativeFaceTintMaterializationResult(
                emptyBuild.Written, emptyBuild.Artifact, diagnostics.ToImmutable());
        }

        var assetAuthorities = request.MaskAuthorities.Select(item =>
            new SkyrimAssetContentAuthority(
                item.ProviderId,
                item.ProviderKind switch
                {
                    AssetProviderKind.Loose => SkyrimAssetContentProviderKind.Loose,
                    AssetProviderKind.Archive => SkyrimAssetContentProviderKind.Bsa,
                    _ => throw new InvalidDataException(
                        $"Unsupported mask provider kind '{item.ProviderKind}'.")
                },
                item.ProviderPath,
                item.ProviderSha256,
                item.AssetPath,
                item.ContentLength,
                item.ContentSha256)).ToImmutableArray();
        SkyrimAssetContentResolutionResult assets;
        try
        {
            assets = await assetResolver.ResolveAsync(
                new SkyrimAssetContentResolutionRequest(
                    request.AssetAllowedRoot, assetAuthorities),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("skyrim-native-tint-asset-resolution",
                exception.Message));
            return Refused(diagnostics);
        }
        diagnostics.AddRange(assets.Diagnostics);
        if (!assets.Resolved || HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        var masks = assets.Assets.Select(item => new SkyrimNativeFaceTintMaskInput(
            item.AssetPath, item.ContentSha256, item.Content)).ToImmutableArray();
        SkyrimNativeFaceTintBuildResult build = await buildService.BuildAsync(
            new SkyrimNativeFaceTintBuildRequest(
                records.Route, masks, request.OutputPath), cancellationToken)
            .ConfigureAwait(false);
        diagnostics.AddRange(build.Diagnostics);
        return new SkyrimNativeFaceTintMaterializationResult(
            build.Written, build.Artifact, diagnostics.ToImmutable());
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimNativeFaceTintMaterializationResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
