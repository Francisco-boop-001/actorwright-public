using System.Collections.Immutable;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

public sealed class PackageInspectService(PackageManifestReader reader) : IPackageInspectService
{
    public async ValueTask<PackageInspectResult> InspectAsync(
        PackageInspectRequest request, CancellationToken cancellationToken)
    {
        var result = await reader.ReadAsync(request.ManifestPath, cancellationToken);
        return new PackageInspectResult(result.Identity is not null &&
            !result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error),
            result.Identity, result.Diagnostics);
    }
}
