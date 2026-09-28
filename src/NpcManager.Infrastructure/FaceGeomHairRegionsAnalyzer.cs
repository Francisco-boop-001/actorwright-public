using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class FaceGeomHairRegionsAnalyzer
{
    private readonly WorkspacePath workspaceRoot;
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;

    public FaceGeomHairRegionsAnalyzer(WorkspacePath workspaceRoot)
        : this(
            workspaceRoot,
            new FaceGeomHairRegionsWorkspaceBoundary(
                workspaceRoot))
    {
    }

    internal FaceGeomHairRegionsAnalyzer(
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsWorkspaceBoundary boundary)
    {
        this.workspaceRoot = workspaceRoot;
        this.boundary = boundary ??
            throw new ArgumentNullException(nameof(boundary));
        if (boundary.WorkspaceRoot != workspaceRoot)
            throw new ArgumentException(
                "The analyzer boundary does not match its workspace.",
                nameof(boundary));
    }

    public async ValueTask<FaceGeomHairRegionsAnalysis> AnalyzeAsync(
        WorkspacePath source,
        Sha256Hash expectedSourceSha256,
        FaceGeomHairRegionPluginColorContext? pluginColorContext,
        CancellationToken cancellationToken)
    {
        (byte[] bytes, FaceGeomHairRegionsFile file) =
            await FaceGeomHairRegionsSupport.ReadSourceAsync(
                boundary,
                source,
                expectedSourceSha256,
                cancellationToken);
        return FaceGeomHairRegionsSupport.AnalyzeBytes(
            file,
            bytes,
            pluginColorContext);
    }

    public FaceGeomHairRegionsRequest CreateAssignmentTemplate(
        StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
            analysisDocument,
        WorkspacePath output,
        WorkspacePath manifest)
    {
        var documents = new FaceGeomHairRegionsDocumentCodec();
        FaceGeomHairRegionsAnalysis analysis =
            documents.ValidateAnalysis(analysisDocument);
        if (!string.Equals(
                analysis.Schema,
                FaceGeomHairRegionSchemas.Analysis,
                StringComparison.Ordinal) ||
            analysis.Regions.IsDefaultOrEmpty ||
            analysis.Regions.Length >
            FaceGeomHairRegionsSupport.MaximumHairTintShapes)
            throw new InvalidDataException(
                "The HairTint analysis document is invalid.");
        FaceGeomHairRegionsSupport.ValidateOutputPaths(
            workspaceRoot,
            analysis.Source.Path,
            output,
            manifest);
        ImmutableArray<FaceGeomHairRegionAssignment> assignments =
            analysis.Regions
                .Select(region =>
                    new FaceGeomHairRegionAssignment(
                        region.StructuralId,
                        FaceGeomHairRegionRole.Preserve))
                .ToImmutableArray();
        return new FaceGeomHairRegionsRequest(
            FaceGeomHairRegionSchemas.Request,
            analysisDocument.Sha256,
            analysis.Source,
            "#000000",
            "#FFFFFF",
            assignments,
            output,
            manifest);
    }
}
