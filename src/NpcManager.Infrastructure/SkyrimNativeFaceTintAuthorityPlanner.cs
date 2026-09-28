using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// FaceTint-specific adapter over the shared Skyrim NIF/TRI/DDS authority
/// planner. It keeps the public mask contract narrow while sharing winner logic.
/// </summary>
public sealed class SkyrimNativeFaceTintAuthorityPlanner(
    IAssetIndexer assetIndexer,
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : ISkyrimNativeFaceTintAuthorityPlanner
{
    private const int MaximumMasks = 256;
    private readonly SkyrimAssetAuthorityPlanner _planner =
        new SkyrimAssetAuthorityPlanner(assetIndexer, workspacePolicy, labRoot);

    public async ValueTask<SkyrimNativeFaceTintAuthorityPlanResult> PlanAsync(
        SkyrimNativeFaceTintAuthorityPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        if (request.RequiredMasks.IsEmpty)
        {
            return new SkyrimNativeFaceTintAuthorityPlanResult(
                true, [], diagnostics.ToImmutable());
        }

        SkyrimAssetAuthorityPlanResult result = await _planner.PlanAsync(
            new SkyrimAssetAuthorityPlanRequest(
                request.Edition, request.DataRoot, request.RequiredMasks),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(result.Diagnostics);
        if (!result.Accepted || HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<SkyrimNativeFaceTintMaskAuthority> authorities =
            result.Authorities.Select(item => new SkyrimNativeFaceTintMaskAuthority(
                item.ProviderId, item.ProviderKind, item.ProviderPath,
                item.ProviderSha256, item.AssetPath, item.ContentLength,
                item.ContentSha256)).ToImmutableArray();
        return new SkyrimNativeFaceTintAuthorityPlanResult(
            true, authorities, diagnostics.ToImmutable());
    }

    private static void ValidateRequest(
        SkyrimNativeFaceTintAuthorityPlanRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("skyrim-native-tint-plan-edition",
                "Native FaceTint mask planning supports Skyrim Special Edition only."));
        }
        if (request.RequiredMasks.IsDefault ||
            request.RequiredMasks.Length > MaximumMasks)
        {
            diagnostics.Add(Error("skyrim-native-tint-plan-mask-count",
                $"RequiredMasks must explicitly contain at most {MaximumMasks} paths."));
            return;
        }
        if (request.RequiredMasks.Any(item =>
                string.IsNullOrWhiteSpace(item.Value) ||
                !item.Value.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ||
                !item.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) ||
            request.RequiredMasks.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.RequiredMasks.Length)
        {
            diagnostics.Add(Error("skyrim-native-tint-plan-mask-shape",
                "RequiredMasks must be distinct canonical textures/*.dds paths."));
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimNativeFaceTintAuthorityPlanResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], diagnostics.ToImmutable());
}
