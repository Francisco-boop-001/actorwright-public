using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Product-owned Skyrim SE FaceGeom carrier assembly. It combines admitted
/// one-shape headpart NIF closures into the standard four-node FaceGen graph,
/// rewrites every block reference and name index, binds the head texture to the
/// canonical FaceTint route, serializes in process, and reparses the result.
/// </summary>
public sealed partial class SseFaceGeomCarrierAssembler :
    ISseFaceGeomCarrierAssembler
{
    public SseFaceGeomCarrierAssemblyResult Assemble(
        SseFaceGeomCarrierAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            if (!ValidateRequest(request, diagnostics)) return Refused(diagnostics);

            var admitted = ImmutableArray.CreateBuilder<AdmittedPart>(
                request.Parts.Length);
            foreach (SseFaceGeomCarrierAssemblyPart part in request.Parts)
            {
                AdmittedPart? result = AdmitPart(part, diagnostics);
                if (result is not null) admitted.Add(result);
            }
            if (admitted.Count == 0)
                diagnostics.Add(Error("sse-facegeom-carrier-admitted-part-count",
                    "Carrier assembly had no renderable selected headpart models after bounded dummy filtering."));
            if (HasErrors(diagnostics)) return Refused(diagnostics);
            ValidateSkeletonCompatibility(admitted, diagnostics);
            if (HasErrors(diagnostics)) return Refused(diagnostics);

            BuiltCarrier built = BuildCarrier(admitted.ToImmutable(),
                request.FaceTintPath, request.SkeletonAuthority);
            byte[] bytes = SerializeCarrier(built.Blocks);
            ImmutableArray<SseFaceGeomCarrierAssemblyShape> shapes =
                VerifyCarrier(bytes, built, request.FaceTintPath, diagnostics);
            if (HasErrors(diagnostics)) return Refused(diagnostics);
            if (request.SkeletonAuthority ==
                SseFaceGeomCarrierSkeletonAuthority.IdentityFaceGenBones)
            {
                diagnostics.Add(new Diagnostic(
                    "sse-facegeom-carrier-identity-skeleton-authority",
                    DiagnosticSeverity.Info,
                    "Assembled the carrier with identity FaceGen bone-node translations; source skin-to-bone transforms remain authoritative."));
            }

            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            var artifact = new SseFaceGeomCarrierAssemblyArtifact(
                ImmutableArray.CreateRange(bytes),
                hash,
                bytes.Length,
                built.Blocks.Length,
                request.FaceTintPath,
                shapes,
                RuntimeAuthority: false);
            diagnostics.Add(new Diagnostic("sse-facegeom-carrier-assembled",
                DiagnosticSeverity.Info,
                $"Assembled and reparsed {shapes.Length} selected headpart shape(s) into a {built.Blocks.Length}-block Skyrim FaceGen carrier."));
            return new SseFaceGeomCarrierAssemblyResult(true, true, artifact,
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           OverflowException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("sse-facegeom-carrier-assembly-failed",
                exception.Message));
            return Refused(diagnostics);
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SseFaceGeomCarrierAssemblyResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, diagnostics.ToImmutable());
}
