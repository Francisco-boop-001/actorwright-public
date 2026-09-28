using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record FinalFaceGeomTexturePlanRequest(
    ImmutableArray<string> ProviderSlots,
    SkyrimFaceTextureSetRecordRoute? TextureSet,
    bool UsesFaceTint,
    AssetPath FaceTintPath);

public sealed record FinalFaceGeomTexturePlan(
    ImmutableArray<string> NifSlots,
    ImmutableArray<AssetPath> RequiredInputTextures,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => Diagnostics.All(item =>
        item.Severity != DiagnosticSeverity.Error);
}

public interface IFinalFaceGeomTexturePlanService
{
    FinalFaceGeomTexturePlan Plan(FinalFaceGeomTexturePlanRequest request);
}
