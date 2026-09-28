using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static class FaceGenDiagnosticCodes
{
    public const string RecordCarrierMismatch = "sse-face-bake-record-carrier-mismatch";
}

/// <summary>Winning copied NPC appearance, resolved through its HCLF to the winning CLFM.</summary>
public sealed record SkyrimFaceGenRecordAppearance(
    PluginName WinningPlugin,
    NpcSex Sex,
    FormReference Race,
    ImmutableArray<FormReference> HeadParts,
    FormReference? HairColor,
    uint? HairColorPackedRgb);

public enum FaceGenShapeRole
{
    Head,
    Hair,
    Collider,
    Body,
    Unknown
}

public static class FaceGenShapeRoleExtensions
{
    public static string ToWireName(this FaceGenShapeRole role) => role switch
    {
        FaceGenShapeRole.Head => "head",
        FaceGenShapeRole.Hair => "hair",
        FaceGenShapeRole.Collider => "collider",
        FaceGenShapeRole.Body => "body",
        FaceGenShapeRole.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported FaceGen shape role.")
    };

    public static bool TryParseWireName(string value, out FaceGenShapeRole role)
    {
        role = value.Trim().ToLowerInvariant() switch
        {
            "head" or "face" => FaceGenShapeRole.Head,
            "hair" => FaceGenShapeRole.Hair,
            "collider" or "collision" => FaceGenShapeRole.Collider,
            "body" => FaceGenShapeRole.Body,
            "unknown" => FaceGenShapeRole.Unknown,
            _ => FaceGenShapeRole.Unknown
        };
        return value.Trim().ToLowerInvariant() is "head" or "face" or "hair" or "collider" or "collision" or "body" or "unknown";
    }
}

public sealed record FaceGenShape(
    string Name,
    FaceGenShapeRole Role,
    AssetPath SourcePath,
    int VertexCount,
    Sha256Hash TopologySha256,
    bool Applicable,
    bool IncludedInOutput);

public sealed record FaceGenManifest(
    GameEdition Edition,
    FormId NpcFormId,
    ImmutableArray<FaceGenShape> Shapes,
    Sha256Hash SourceHash);

public sealed record FaceGenDiagnoseRequest(GameEdition Edition, WorkspacePath ManifestPath, FormId? NpcFormId);

public sealed record FaceGenVerifyRequest(GameEdition Edition, WorkspacePath ManifestPath, FormId? NpcFormId, bool StrictShapes);

public sealed record FaceGenAnalysisResult(
    bool IsApplicable,
    int ValidHeadShapeCount,
    FaceGenManifest? Manifest,
    ImmutableArray<FaceGenShape> AcceptedShapes,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenService
{
    ValueTask<FaceGenAnalysisResult> DiagnoseAsync(FaceGenDiagnoseRequest request, CancellationToken cancellationToken);

    ValueTask<FaceGenAnalysisResult> VerifyAsync(FaceGenVerifyRequest request, CancellationToken cancellationToken);
}
