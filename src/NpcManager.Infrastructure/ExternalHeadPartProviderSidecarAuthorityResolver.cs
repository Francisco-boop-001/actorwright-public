using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Admits only the hash-bound Dint HDT XML sidecar that is known to belong to
/// the external provider route. XML is evidence for the provider, never a
/// FaceGeom NIF/TRI/DDS asset.
/// </summary>
public sealed class ExternalHeadPartProviderSidecarAuthorityResolver(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : IExternalHeadPartProviderSidecarAuthorityResolver
{
    public const string AdmittedSidecarPath =
        "meshes/armor/[dint999]/02 Hair/wig/16/16.xml";
    public const long AdmittedSidecarLength = 156939;
    public const string AdmittedSidecarSha256 =
        "67C23C1021166B789265DE5B0B34A1E69D4F7AE133BB852A73E6AE74F50D9057";

    public ExternalHeadPartProviderSidecarAuthorityResult Resolve(
        ExternalHeadPartProviderSidecarAuthorityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(
            labRoot, request.DataRoot));

        if (!string.Equals(request.SidecarPath.Value, AdmittedSidecarPath,
                StringComparison.OrdinalIgnoreCase) ||
            request.SidecarPath.Value.Contains('\\') ||
            request.SidecarPath.Value.Contains("..", StringComparison.Ordinal) ||
            request.SidecarPath.Value.Contains(':'))
        {
            diagnostics.Add(Error(
                "external-headpart-sidecar-path",
                $"Only the admitted Dint provider sidecar '{AdmittedSidecarPath}' is allowed."));
        }

        if (!string.Equals(
                request.ExternalHeadPartReference.Plugin.Value,
                SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            request.ExternalHeadPartReference.FormId.Value != 0xBC05 ||
            !string.Equals(
                request.ProviderPlugin.Value,
                SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.ProviderPluginSha256.Value,
                SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPluginSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "external-headpart-sidecar-provider",
                "The provider sidecar requires the exact admitted Dint HDPT and plugin SHA-256 route."));
        }

        if (HasErrors(diagnostics)) return Refused(diagnostics);

        string dataRoot = Path.GetFullPath(request.DataRoot.Value);
        string physical = Path.GetFullPath(Path.Combine(
            dataRoot,
            request.SidecarPath.Value.Replace('/', Path.DirectorySeparatorChar)));
        var physicalPath = new WorkspacePath(physical);
        if (!physicalPath.IsUnder(request.DataRoot) ||
            physicalPath == request.DataRoot)
        {
            diagnostics.Add(Error(
                "external-headpart-sidecar-root",
                "The provider sidecar escaped the copied K-local Data root."));
            return Refused(diagnostics);
        }
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(
            labRoot, physicalPath));

        FileInfo info = new(physical);
        if (!info.Exists || !IsOrdinaryLooseFile(info.Attributes))
        {
            diagnostics.Add(Error(
                "external-headpart-sidecar-file",
                "The admitted provider sidecar must be a present ordinary loose file, not a directory or reparse point."));
            return Refused(diagnostics);
        }
        if (info.Length != AdmittedSidecarLength)
        {
            diagnostics.Add(Error(
                "external-headpart-sidecar-size",
                $"The admitted provider sidecar is {info.Length} bytes, not {AdmittedSidecarLength}."));
            return Refused(diagnostics);
        }

        try
        {
            using FileStream stream = new(
                physical, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            Sha256Hash actual = new(Convert.ToHexString(SHA256.HashData(stream)));
            if (!string.Equals(actual.Value, AdmittedSidecarSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "external-headpart-sidecar-hash",
                    $"The admitted provider sidecar hash {actual} does not match {AdmittedSidecarSha256}."));
                return Refused(diagnostics);
            }

            return new ExternalHeadPartProviderSidecarAuthorityResult(
                true,
                new SkyrimAssetAuthority(
                    "loose",
                    AssetProviderKind.Loose,
                    physicalPath,
                    actual,
                    request.SidecarPath,
                    info.Length,
                    actual),
                diagnostics.ToImmutable());
        }
        catch (IOException exception)
        {
            diagnostics.Add(Error(
                "external-headpart-sidecar-read",
                exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(Error(
                "external-headpart-sidecar-read",
                exception.Message));
            return Refused(diagnostics);
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    internal static bool IsOrdinaryLooseFile(FileAttributes attributes) =>
        !attributes.HasFlag(FileAttributes.Directory) &&
        !attributes.HasFlag(FileAttributes.ReparsePoint);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ExternalHeadPartProviderSidecarAuthorityResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
