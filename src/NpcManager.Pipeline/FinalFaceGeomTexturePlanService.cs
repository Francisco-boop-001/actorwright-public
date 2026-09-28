using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>Composes provider, winning TXST, and generated FaceTint authority.</summary>
public sealed class FinalFaceGeomTexturePlanService :
    IFinalFaceGeomTexturePlanService
{
    private static readonly int[] RawTxToNifSlot = [0, 1, 5, 2, 3, 4, 6, 7];

    public FinalFaceGeomTexturePlan Plan(FinalFaceGeomTexturePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.ProviderSlots.IsDefaultOrEmpty ||
            request.ProviderSlots.Length > 32)
            diagnostics.Add(Error("carrier-texture-slot-shape",
                "Provider texture slots must contain 1 through 32 entries."));
        if (request.TextureSet is { RawTxSlots.Length: not 8 })
            diagnostics.Add(Error("headpart-texture-set-shape",
                "A winning TXST route must contain exactly eight TX00-TX07 entries."));
        if (request.TextureSet is not null && request.ProviderSlots.Length < 8)
            diagnostics.Add(Error("carrier-texture-slot-capacity",
                "A TXST override requires at least eight provider NIF texture slots."));
        if (request.UsesFaceTint && request.ProviderSlots.Length <= 6)
            diagnostics.Add(Error("carrier-texture-slot-capacity",
                "A FaceTint owner requires provider NIF texture slot 6."));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new FinalFaceGeomTexturePlan([], [], diagnostics.ToImmutable());

        string[] slots = request.ProviderSlots.ToArray();
        if (request.TextureSet is { } textureSet)
        {
            for (int rawSlot = 0; rawSlot < textureSet.RawTxSlots.Length; rawSlot++)
                slots[RawTxToNifSlot[rawSlot]] = textureSet.RawTxSlots[rawSlot];
        }
        if (request.UsesFaceTint)
            slots[6] = request.FaceTintPath.Value.Replace('/', '\\');

        var inputs = ImmutableArray.CreateBuilder<AssetPath>();
        foreach (string route in slots)
        {
            if (string.IsNullOrEmpty(route) ||
                string.Equals(route,
                    request.FaceTintPath.Value.Replace('/', '\\'),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                string canonical = route.Replace('\\', '/').TrimStart('/');
                if (!canonical.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
                    canonical = "textures/" + canonical;
                inputs.Add(new AssetPath(canonical));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error("facegeom-final-texture-path",
                    $"Final texture route '{route}' is invalid: {exception.Message}"));
            }
        }

        return new FinalFaceGeomTexturePlan(
            slots.ToImmutableArray(),
            inputs.DistinctBy(item => item.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            diagnostics.ToImmutable());
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
