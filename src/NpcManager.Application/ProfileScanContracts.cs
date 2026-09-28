using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Describes a read-only fingerprint request for a copied game profile.</summary>
public sealed record ProfileScanRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    WorkspacePath? LoadOrderPath = null);

public sealed record ProfilePluginFingerprint(
    PluginName Plugin,
    long Bytes,
    Sha256Hash Sha256);

public sealed record ProfileScanResult(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<ProfilePluginFingerprint> Plugins,
    Sha256Hash? LoadOrderSha256,
    bool? LoadOrderValid,
    Sha256Hash Fingerprint,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IProfileScanService
{
    ValueTask<ProfileScanResult> ScanAsync(ProfileScanRequest request, CancellationToken cancellationToken);
}
