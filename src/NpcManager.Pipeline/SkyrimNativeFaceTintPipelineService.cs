using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Automatic product path for one copied-Data native Skyrim FaceTint build.
/// The first record pass discovers the exact mask closure; the second pass at
/// materialization revalidates the hash-bound records immediately before write.
/// </summary>
public sealed class SkyrimNativeFaceTintPipelineService(
    ISkyrimFaceRecordPluginAuthorityLoader pluginAuthorityLoader,
    ISkyrimNativeFaceTintRecordResolver recordResolver,
    ISkyrimNativeFaceTintAuthorityPlanner maskAuthorityPlanner,
    ISkyrimNativeFaceTintMaterializationService materializationService)
    : ISkyrimNativeFaceTintPipelineService
{
    public async ValueTask<SkyrimNativeFaceTintPipelineResult> BuildAsync(
        SkyrimNativeFaceTintPipelineRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        SkyrimFaceRecordPluginAuthorityResult plugins = await pluginAuthorityLoader.LoadAsync(
            new SkyrimFaceRecordPluginAuthorityRequest(
                request.Edition, request.DataRoot, request.PluginOrder)
            {
                StagedPluginAuthorities = request.StagedPluginAuthorities
            },
            cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, plugins.Diagnostics);
        if (!plugins.Accepted || HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        var recordRequest = new SkyrimNativeFaceTintRecordRequest(
            request.Edition, request.Npc, request.ExpectedSex,
            request.ExpectedRace, plugins.Authorities,
            request.MaskOverrides.IsDefault ? [] : request.MaskOverrides);
        SkyrimNativeFaceTintRecordResult records = await recordResolver.ResolveAsync(
            recordRequest, cancellationToken).ConfigureAwait(false);
        if (!records.Accepted || records.Route is null || HasErrors(records.Diagnostics))
        {
            AddDistinct(diagnostics, records.Diagnostics);
            return Refused(diagnostics);
        }
        if (records.Route.Layers.IsDefault)
        {
            AddDistinct(diagnostics,
            [
                new Diagnostic("skyrim-native-tint-route-shape", DiagnosticSeverity.Error,
                    "The accepted record route returned an invalid layer array.")
            ]);
            return Refused(diagnostics);
        }

        ImmutableArray<AssetPath> masks = records.Route.Layers
            .Where(item => item.Coverage > 0F)
            .Select(item => item.MaskPath)
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        ImmutableArray<SkyrimNativeFaceTintMaskAuthority> maskAuthorities = [];
        if (!masks.IsEmpty)
        {
            SkyrimNativeFaceTintAuthorityPlanResult plan = await maskAuthorityPlanner.PlanAsync(
                new SkyrimNativeFaceTintAuthorityPlanRequest(
                    request.Edition, request.DataRoot, masks),
                cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, plan.Diagnostics);
            if (!plan.Accepted || HasErrors(diagnostics))
            {
                return Refused(diagnostics);
            }
            maskAuthorities = plan.Authorities;
        }

        SkyrimNativeFaceTintMaterializationResult materialized =
            await materializationService.MaterializeAsync(
                new SkyrimNativeFaceTintMaterializationRequest(
                    recordRequest, request.DataRoot, maskAuthorities, request.OutputPath),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, materialized.Diagnostics);
        return new SkyrimNativeFaceTintPipelineResult(
            materialized.Written, materialized.Artifact, plugins.Authorities,
            maskAuthorities, diagnostics.ToImmutable());
    }

    private static void AddDistinct(
        ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source)
    {
        foreach (Diagnostic diagnostic in source)
        {
            if (!target.Any(existing => existing.Code == diagnostic.Code &&
                                        existing.Severity == diagnostic.Severity &&
                                        existing.Message == diagnostic.Message))
            {
                target.Add(diagnostic);
            }
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimNativeFaceTintPipelineResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, [], [], diagnostics.ToImmutable());
}
