using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Uses the in-process uncompressed DDS reader first and invokes the secondary
/// codec only when the source is a well-formed but unsupported pixel format.
/// Security, path, identity, size, and I/O refusals never fall through.
/// </summary>
public sealed class ChainedFaceTintTextureDecoder(
    IFaceTintTextureDecoder primary,
    IFaceTintTextureDecoder secondary) : IFaceTintTextureDecoder
{
    public async ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
        WorkspacePath sourceDds,
        CancellationToken cancellationToken)
    {
        FaceTintTextureDecodeResult first = await primary.DecodeAsync(
            sourceDds, cancellationToken);
        if (first.Decoded || !MayTrySecondary(first.Diagnostics))
            return first;

        FaceTintTextureDecodeResult second = await secondary.DecodeAsync(
            sourceDds, cancellationToken);
        if (!second.Decoded)
        {
            return second with
            {
                Diagnostics = first.Diagnostics.AddRange(second.Diagnostics)
            };
        }

        return second with
        {
            Diagnostics = second.Diagnostics.Insert(0, new Diagnostic(
                "facetint-decoder-secondary-format",
                DiagnosticSeverity.Info,
                "The source DDS requires the declared secondary compressed-texture codec."))
        };
    }

    private static bool MayTrySecondary(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Length == 1 &&
        diagnostics[0] is
        {
            Code: "facetint-bgra8-format-refused",
            Severity: DiagnosticSeverity.Error
        };
}
