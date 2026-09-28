using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class FaceGeomHairRegionsProposer
{
    private readonly WorkspacePath workspaceRoot;
    private readonly FaceGeomHairRegionsDocumentCodec documents;

    public FaceGeomHairRegionsProposer(WorkspacePath workspaceRoot)
        : this(
            workspaceRoot,
            new FaceGeomHairRegionsDocumentCodec())
    {
    }

    public FaceGeomHairRegionsProposer(
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsDocumentCodec documents)
    {
        this.workspaceRoot = workspaceRoot;
        this.documents = documents ??
            throw new ArgumentNullException(nameof(documents));
    }

    public async ValueTask<FaceGeomHairRegionsProposalResult>
        ProposeAsync(
        StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
            analysisDocument,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            requestDocument,
        CancellationToken cancellationToken)
    {
        FaceGeomHairRegionsAnalysis analysis =
            documents.ValidateAnalysis(analysisDocument);
        FaceGeomHairRegionsRequest request =
            documents.ValidateRequest(requestDocument);
        ValidateDocumentHeaders(
            analysis,
            analysisDocument.Sha256,
            request);
        FaceGeomHairRegionsSupport.ValidateOutputPaths(
            workspaceRoot,
            request.Source.Path,
            request.Output,
            request.Manifest);

        byte[] source =
            await FaceGeomHairRegionsSupport.ReadBoundSourceAsync(
                documents.WorkspaceBoundary,
                request.Source,
                cancellationToken);
        FaceGeomHairRegionsAnalysis actual =
            FaceGeomHairRegionsSupport.AnalyzeBytes(
                request.Source,
                source,
                analysis.PluginColorContext);
        if (!AnalysisMatches(analysis, actual))
            throw new InvalidDataException(
                "The analysis no longer matches the reopened FaceGeom source.");

        ImmutableDictionary<string, FaceGeomHairRegionRole> roles =
            ValidateAssignments(actual, request);
        ImmutableArray<uint> primaryBits =
            FaceGeomHairRegionsSupport.ParseColorBits(
                request.PrimaryColor);
        ImmutableArray<uint> accentBits =
            FaceGeomHairRegionsSupport.ParseColorBits(
                request.AccentColor);

        byte[] predicted = source.ToArray();
        var envelopes = ImmutableArray.CreateBuilder<
            FaceGeomHairRegionsAuthorizedEnvelope>();
        foreach (IGrouping<string, FaceGeomHairRegionsRegion> group in
                 actual.Regions
                     .GroupBy(region =>
                         region.SharedShaderGroupId)
                     .OrderBy(group =>
                         group.Min(region =>
                             region.TintByteOffset)))
        {
            FaceGeomHairRegionRole role =
                roles[group.First().StructuralId];
            if (role == FaceGeomHairRegionRole.Preserve)
                continue;
            ImmutableArray<uint> newBits =
                role == FaceGeomHairRegionRole.Primary
                    ? primaryBits
                    : accentBits;
            FaceGeomHairRegionsRegion representative =
                group.First();
            FaceGeomHairRegionsSupport.WriteFloatBits(
                predicted,
                representative.TintByteOffset,
                newBits);
            envelopes.Add(
                new FaceGeomHairRegionsAuthorizedEnvelope(
                    representative.SharedShaderGroupId,
                    group.Select(region =>
                            region.StructuralId)
                        .Order(StringComparer.Ordinal)
                        .ToImmutableArray(),
                    role,
                    representative.TintByteOffset,
                    12,
                    representative.ColorFloatBits,
                    newBits));
        }

        ImmutableArray<int> changed =
            FaceGeomHairRegionsSupport.Diff(source, predicted);
        if (changed.IsEmpty)
            throw new InvalidDataException(
                "The completed HairTint request is a no-op transaction.");
        if (changed.Any(offset =>
                !envelopes.Any(envelope =>
                    offset >= envelope.ByteOffset &&
                    offset <
                    envelope.ByteOffset +
                    envelope.ByteLength)))
            throw new InvalidDataException(
                "HairTint simulation escaped an authorized 12-byte envelope.");

        SseNifDocument predictedDocument =
            SseFaceGeomCarrierCodec.Parse(predicted);
        FaceGeomHairRegionsFingerprints expectedFingerprints =
            FaceGeomHairRegionsSupport.ComputeFingerprints(
                predictedDocument);
        if (actual.Fingerprints != expectedFingerprints)
            throw new InvalidDataException(
                "HairTint simulation changed an invariant NIF fingerprint.");

        var proposal = new FaceGeomHairRegionsProposal(
            FaceGeomHairRegionSchemas.Proposal,
            analysisDocument.Sha256,
            requestDocument.Sha256,
            request.Source,
            request.Output,
            request.Manifest,
            request.PrimaryColor,
            request.AccentColor,
            request.Assignments,
            envelopes.ToImmutable(),
            changed,
            actual.Fingerprints,
            expectedFingerprints,
            new FaceGeomHairRegionsFile(
                request.Output,
                predicted.LongLength,
                FaceGeomHairRegionsSupport.Hash(predicted)),
            analysis.PluginColorContext);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (analysis.PluginColorContext?.HairColorHex is
            string pluginColor)
        {
            FaceGeomHairRegionsSupport.ValidateColor(
                pluginColor,
                "PluginColorContext.HairColorHex");
            if (!string.Equals(
                    pluginColor,
                    request.PrimaryColor,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(
                    new Diagnostic(
                        "facegeom-hair-regions-plugin-color-differs",
                        DiagnosticSeverity.Warning,
                        "The read-only plugin HairColor context differs from PrimaryColor; this transaction does not write the plugin."));
            }
        }
        return new FaceGeomHairRegionsProposalResult(
            documents.BindProposal(proposal),
            diagnostics.ToImmutable());
    }

    private static void ValidateDocumentHeaders(
        FaceGeomHairRegionsAnalysis analysis,
        Sha256Hash analysisSha256,
        FaceGeomHairRegionsRequest request)
    {
        if (!string.Equals(
                analysis.Schema,
                FaceGeomHairRegionSchemas.Analysis,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.Schema,
                FaceGeomHairRegionSchemas.Request,
                StringComparison.Ordinal) ||
            request.AnalysisSha256 != analysisSha256 ||
            request.Source != analysis.Source)
            throw new InvalidDataException(
                "The HairTint analysis/request authority chain is invalid.");
        FaceGeomHairRegionsSupport.ValidateColor(
            request.PrimaryColor,
            "PrimaryColor");
        FaceGeomHairRegionsSupport.ValidateColor(
            request.AccentColor,
            "AccentColor");
        if (string.Equals(
                request.PrimaryColor,
                request.AccentColor,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "PrimaryColor and AccentColor must be distinct.");
    }

    private static ImmutableDictionary<string, FaceGeomHairRegionRole>
        ValidateAssignments(
            FaceGeomHairRegionsAnalysis analysis,
            FaceGeomHairRegionsRequest request)
    {
        if (request.Assignments.IsDefault ||
            request.Assignments.Length !=
            analysis.Regions.Length ||
            request.Assignments.Any(assignment =>
                string.IsNullOrWhiteSpace(
                    assignment.StructuralId) ||
                !Enum.IsDefined(assignment.Role)) ||
            request.Assignments
                .Select(assignment =>
                    assignment.StructuralId)
                .Distinct(StringComparer.Ordinal)
                .Count() != request.Assignments.Length)
            throw new InvalidDataException(
                "Every analyzed HairTint shape must be classified exactly once.");
        ImmutableDictionary<string, FaceGeomHairRegionRole> roles =
            request.Assignments.ToImmutableDictionary(
                assignment => assignment.StructuralId,
                assignment => assignment.Role,
                StringComparer.Ordinal);
        if (analysis.Regions.Any(region =>
                !roles.ContainsKey(region.StructuralId)) ||
            roles.Keys.Any(structuralId =>
                analysis.Regions.All(region =>
                    !string.Equals(
                        region.StructuralId,
                        structuralId,
                        StringComparison.Ordinal))))
            throw new InvalidDataException(
                "HairTint assignments do not match the analyzed shape inventory.");
        foreach (IGrouping<string, FaceGeomHairRegionsRegion> group in
                 analysis.Regions.GroupBy(region =>
                     region.SharedShaderGroupId))
        {
            if (group.Select(region =>
                        roles[region.StructuralId])
                    .Distinct()
                    .Count() != 1)
                throw new InvalidDataException(
                    $"Shared shader group {group.Key} has conflicting roles.");
        }

        FaceGeomHairRegionRole[] physicalRoles = analysis.Regions
            .GroupBy(region => region.SharedShaderGroupId)
            .Select(group =>
                roles[group.First().StructuralId])
            .ToArray();
        if (!physicalRoles.Contains(
                FaceGeomHairRegionRole.Primary) ||
            !physicalRoles.Contains(
                FaceGeomHairRegionRole.Accent))
            throw new InvalidDataException(
                "At least one physical shader group must be Primary and one must be Accent.");
        return roles;
    }

    private static bool AnalysisMatches(
        FaceGeomHairRegionsAnalysis expected,
        FaceGeomHairRegionsAnalysis actual)
    {
        if (expected.Source != actual.Source ||
            expected.Fingerprints != actual.Fingerprints ||
            expected.Regions.Length != actual.Regions.Length)
            return false;
        for (int index = 0;
             index < expected.Regions.Length;
             index++)
        {
            FaceGeomHairRegionsRegion left =
                expected.Regions[index];
            FaceGeomHairRegionsRegion right =
                actual.Regions[index];
            if (left.StructuralId != right.StructuralId ||
                left.Name != right.Name ||
                left.DuplicateNameOrdinal !=
                right.DuplicateNameOrdinal ||
                left.ShapeBlockType != right.ShapeBlockType ||
                left.ShapeBlockId != right.ShapeBlockId ||
                left.ShapeShaderReferenceByteOffset !=
                right.ShapeShaderReferenceByteOffset ||
                left.ShaderBlockType !=
                right.ShaderBlockType ||
                left.ShaderBlockId != right.ShaderBlockId ||
                left.SharedShaderGroupId !=
                right.SharedShaderGroupId ||
                left.TextureSetBlockId !=
                right.TextureSetBlockId ||
                left.TintByteOffset != right.TintByteOffset ||
                left.TintByteLength !=
                right.TintByteLength ||
                left.CurrentColor != right.CurrentColor ||
                left.DefaultRole != right.DefaultRole ||
                !left.ColorFloatBits.SequenceEqual(
                    right.ColorFloatBits) ||
                !left.TextureRoutes.SequenceEqual(
                    right.TextureRoutes,
                    StringComparer.Ordinal) ||
                !left.SharedStructuralIds.SequenceEqual(
                    right.SharedStructuralIds,
                    StringComparer.Ordinal) ||
                !left.ShaderOwnerStructuralIds.SequenceEqual(
                    right.ShaderOwnerStructuralIds,
                    StringComparer.Ordinal))
                return false;
        }
        return true;
    }
}
