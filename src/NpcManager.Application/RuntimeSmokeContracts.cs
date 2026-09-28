using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record RuntimeSmokeVerifyRequest(
    GameEdition Edition,
    WorkspacePath RuntimeReport,
    WorkspacePath PackageAcceptance);

public sealed record RuntimeSmokeVerifyAllRequest(
    WorkspacePath Fallout4Report,
    WorkspacePath SkyrimSeReport,
    WorkspacePath PackageAcceptance);

public sealed record RuntimeSmokeVerifyResult(
    bool IsValid,
    string SchemaVersion,
    string Edition,
    string RuntimeReport,
    string PackageAcceptance,
    ImmutableArray<string> ValidatedScreenshots,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record RuntimeSmokeVerifyAllResult(
    bool IsValid,
    RuntimeSmokeVerifyResult Fallout4,
    RuntimeSmokeVerifyResult SkyrimSe,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRuntimeSmokeVerifyService
{
    ValueTask<RuntimeSmokeVerifyResult> VerifyAsync(
        RuntimeSmokeVerifyRequest request,
        CancellationToken cancellationToken);
}
