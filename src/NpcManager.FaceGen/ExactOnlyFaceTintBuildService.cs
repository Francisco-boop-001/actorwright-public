using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Fail-closed capability guard for preset-derived NPC creation.
/// </summary>
/// <remarks>
/// The surrounding package transaction materializes an admitted exact FaceTint
/// without calling <see cref="IFaceTintBuildService"/>. Injecting this guard
/// prevents that creation path from falling back to a generated FaceTint
/// implementation that may own an external encoder or another process-capable
/// dependency.
/// </remarks>
public sealed class ExactOnlyFaceTintBuildService : IFaceTintBuildService
{
    public ValueTask<FaceTintBuildResult> BuildAsync(
        FaceTintBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new FaceTintBuildResult(
            false,
            null,
            null,
            ImmutableArray.Create(new Diagnostic(
                "facetint-exact-source-required",
                DiagnosticSeverity.Error,
                "Preset-derived NPC creation accepts only an exact, independently decoded FaceTint source."))));
    }
}
