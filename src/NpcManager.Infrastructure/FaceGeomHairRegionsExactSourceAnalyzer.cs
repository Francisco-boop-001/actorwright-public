using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Desktop source intake reads an unbound, user-selected K-local FaceGeom
/// exactly once, then derives its path/length/hash authority from those same
/// bytes before structural analysis.
/// </summary>
public sealed class FaceGeomHairRegionsExactSourceAnalyzer
{
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;

    public FaceGeomHairRegionsExactSourceAnalyzer(
        WorkspacePath workspaceRoot)
    {
        boundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                workspaceRoot);
    }

    public async ValueTask<FaceGeomHairRegionsAnalysis> AnalyzeAsync(
        WorkspacePath source,
        FaceGeomHairRegionPluginColorContext? pluginColorContext,
        CancellationToken cancellationToken)
    {
        byte[] bytes =
            await boundary.ReadExactFileAsync(
                source,
                FaceGeomHairRegionsSupport.MaximumSourceBytes,
                "desktop HairTint FaceGeom source",
                cancellationToken);
        if (bytes.LongLength is <= 0 or
            > FaceGeomHairRegionsSupport.MaximumSourceBytes)
        {
            throw new InvalidDataException(
                "The desktop HairTint FaceGeom source is outside its admitted byte range.");
        }
        var file = new FaceGeomHairRegionsFile(
            new WorkspacePath(
                Path.GetFullPath(source.Value)),
            bytes.LongLength,
            new Sha256Hash(
                Convert.ToHexString(
                    SHA256.HashData(bytes))));
        return FaceGeomHairRegionsSupport.AnalyzeBytes(
            file,
            bytes,
            pluginColorContext);
    }
}
