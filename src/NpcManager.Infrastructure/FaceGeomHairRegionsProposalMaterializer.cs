using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Performs the exact canonical authority, source preimage, fixed-width
/// envelope, and predicted-output checks shared by Preview and Apply. It never
/// writes the proposed final output.
/// </summary>
public sealed class FaceGeomHairRegionsProposalMaterializer
{
    private readonly WorkspacePath workspaceRoot;
    private readonly FaceGeomHairRegionsDocumentCodec documents;

    public FaceGeomHairRegionsProposalMaterializer(
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsDocumentCodec documents)
    {
        this.workspaceRoot = workspaceRoot;
        this.documents = documents ??
            throw new ArgumentNullException(nameof(documents));
    }

    public async ValueTask<
        FaceGeomHairRegionsProposalMaterialization> MaterializeAsync(
            StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
                requestDocument,
            StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
                proposalDocument,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestDocument);
        ArgumentNullException.ThrowIfNull(proposalDocument);
        FaceGeomHairRegionsRequest request =
            documents.ValidateRequest(requestDocument);
        FaceGeomHairRegionsProposal proposal =
            documents.ValidateProposal(proposalDocument);
        ValidateAuthorityChain(
            request,
            requestDocument.Sha256,
            proposal);
        FaceGeomHairRegionsSupport.ValidateOutputPaths(
            workspaceRoot,
            proposal.Source.Path,
            proposal.Output,
            proposal.Manifest);
        byte[] source =
            await FaceGeomHairRegionsSupport.ReadBoundSourceAsync(
                documents.WorkspaceBoundary,
                proposal.Source,
                cancellationToken);
        SseNifDocument sourceDocument =
            SseFaceGeomCarrierCodec.Parse(source);
        FaceGeomHairRegionsFingerprints sourceFingerprints =
            FaceGeomHairRegionsSupport.ComputeFingerprints(
                sourceDocument);
        if (sourceFingerprints != proposal.SourceFingerprints)
            throw new InvalidDataException(
                "Materialization rejected the source NIF fingerprints.");
        FaceGeomHairRegionsAnalysis actual =
            FaceGeomHairRegionsSupport.AnalyzeBytes(
                proposal.Source,
                source,
                pluginColorContext: null);
        ValidateSourceDerivedPlan(actual, proposal);

        byte[] candidate = source.ToArray();
        ValidateAndPatch(source, candidate, proposal);
        ImmutableArray<int> changed =
            FaceGeomHairRegionsSupport.Diff(source, candidate);
        if (!changed.SequenceEqual(
                proposal.PredictedChangedByteOffsets) ||
            candidate.LongLength !=
            proposal.ExpectedOutput.ByteLength)
            throw new InvalidDataException(
                "Materialization does not match the proposal.");
        FaceGeomHairRegionsSupport.RequireHash(
            candidate,
            proposal.ExpectedOutput.Sha256,
            "predicted FaceGeom output");
        FaceGeomHairRegionsFingerprints candidateFingerprints =
            FaceGeomHairRegionsSupport.ComputeFingerprints(
                SseFaceGeomCarrierCodec.Parse(candidate));
        if (candidateFingerprints !=
            proposal.ExpectedOutputFingerprints)
            throw new InvalidDataException(
                "Materialization changed an invariant NIF fingerprint.");

        return new FaceGeomHairRegionsProposalMaterialization(
            source.ToImmutableArray(),
            candidate.ToImmutableArray(),
            new FaceGeomHairRegionsFile(
                proposal.Output,
                candidate.LongLength,
                FaceGeomHairRegionsSupport.Hash(candidate)),
            changed,
            sourceFingerprints,
            candidateFingerprints);
    }

    private static void ValidateProposalHeader(
        FaceGeomHairRegionsProposal proposal)
    {
        if (!string.Equals(
                proposal.Schema,
                FaceGeomHairRegionSchemas.Proposal,
                StringComparison.Ordinal) ||
            proposal.Source.ByteLength is <= 0 or
                > FaceGeomHairRegionsSupport.MaximumSourceBytes ||
            proposal.ExpectedOutput.ByteLength !=
            proposal.Source.ByteLength ||
            proposal.ExpectedOutput.Path != proposal.Output ||
            proposal.AuthorizedEnvelopes.IsDefaultOrEmpty ||
            proposal.AuthorizedEnvelopes.Length >
            FaceGeomHairRegionsSupport.MaximumHairTintShapes ||
            proposal.PredictedChangedByteOffsets.IsDefaultOrEmpty)
            throw new InvalidDataException(
                "The HairTint proposal header is invalid.");
        FaceGeomHairRegionsSupport.ValidateColor(
            proposal.PrimaryColor,
            "PrimaryColor");
        FaceGeomHairRegionsSupport.ValidateColor(
            proposal.AccentColor,
            "AccentColor");
        if (proposal.PrimaryColor == proposal.AccentColor)
            throw new InvalidDataException(
                "Proposal HairTint colors must be distinct.");
    }

    internal static void ValidateAuthorityChain(
        FaceGeomHairRegionsRequest request,
        Sha256Hash requestSha256,
        FaceGeomHairRegionsProposal proposal)
    {
        ValidateProposalHeader(proposal);
        if (!string.Equals(
                request.Schema,
                FaceGeomHairRegionSchemas.Request,
                StringComparison.Ordinal) ||
            proposal.RequestSha256 != requestSha256 ||
            proposal.AnalysisSha256 != request.AnalysisSha256 ||
            proposal.Source != request.Source ||
            proposal.Output != request.Output ||
            proposal.Manifest != request.Manifest ||
            !string.Equals(
                proposal.PrimaryColor,
                request.PrimaryColor,
                StringComparison.Ordinal) ||
            !string.Equals(
                proposal.AccentColor,
                request.AccentColor,
                StringComparison.Ordinal) ||
            proposal.Assignments.Length !=
            request.Assignments.Length)
            throw new InvalidDataException(
                "The request and proposal authority chain is invalid.");
        var structuralIds = new HashSet<string>(
            StringComparer.Ordinal);
        for (int index = 0;
             index < request.Assignments.Length;
             index++)
        {
            if (request.Assignments[index] !=
                    proposal.Assignments[index] ||
                string.IsNullOrWhiteSpace(
                    request.Assignments[index].StructuralId) ||
                !structuralIds.Add(
                    request.Assignments[index].StructuralId))
                throw new InvalidDataException(
                    "The proposal does not preserve unique exact request assignments.");
        }
    }

    private static void ValidateAndPatch(
        byte[] source,
        byte[] output,
        FaceGeomHairRegionsProposal proposal)
    {
        var occupied = new HashSet<int>();
        var represented = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (FaceGeomHairRegionsAuthorizedEnvelope envelope in
                 proposal.AuthorizedEnvelopes)
        {
            if (envelope.Role is not (
                    FaceGeomHairRegionRole.Primary or
                    FaceGeomHairRegionRole.Accent) ||
                envelope.ByteLength != 12 ||
                envelope.OldFloatBits.Length != 3 ||
                envelope.NewFloatBits.Length != 3 ||
                envelope.StructuralIds.IsDefaultOrEmpty ||
                envelope.ByteOffset < 0 ||
                envelope.ByteOffset > source.LongLength - 12)
                throw new InvalidDataException(
                    "Materialization rejected an invalid authorized envelope.");
            ImmutableArray<uint> actualOld =
                FaceGeomHairRegionsSupport.ReadFloatBits(
                    source,
                    checked((int)envelope.ByteOffset));
            if (!actualOld.SequenceEqual(envelope.OldFloatBits))
                throw new InvalidDataException(
                    "Materialization rejected stale source HairTint bits.");
            foreach (string structuralId in
                     envelope.StructuralIds)
            {
                if (!represented.Add(structuralId) ||
                    !proposal.Assignments.Any(assignment =>
                        assignment.StructuralId == structuralId &&
                        assignment.Role == envelope.Role))
                    throw new InvalidDataException(
                        "An authorized envelope does not match one exact assignment group.");
            }
            for (int index = 0; index < 12; index++)
                if (!occupied.Add(
                        checked((int)envelope.ByteOffset + index)))
                    throw new InvalidDataException(
                        "Materialization rejected overlapping HairTint envelopes.");
            ImmutableArray<uint> derivedNew =
                FaceGeomHairRegionsSupport.ParseColorBits(
                    envelope.Role ==
                    FaceGeomHairRegionRole.Primary
                        ? proposal.PrimaryColor
                        : proposal.AccentColor);
            if (!derivedNew.SequenceEqual(
                    envelope.NewFloatBits))
                throw new InvalidDataException(
                    "Materialization rejected target bits that do not derive from the selected color.");
            FaceGeomHairRegionsSupport.WriteFloatBits(
                output,
                envelope.ByteOffset,
                derivedNew);
        }
        if (proposal.Assignments.Any(assignment =>
                assignment.Role != FaceGeomHairRegionRole.Preserve &&
                !represented.Contains(assignment.StructuralId)))
            throw new InvalidDataException(
                "A non-Preserve assignment lacks an authorized envelope.");
    }

    private static void ValidateSourceDerivedPlan(
        FaceGeomHairRegionsAnalysis actual,
        FaceGeomHairRegionsProposal proposal)
    {
        if (actual.Fingerprints != proposal.SourceFingerprints ||
            proposal.Assignments.Length != actual.Regions.Length)
            throw new InvalidDataException(
                "The proposal does not cover the exact source-derived HairTint inventory.");
        ImmutableDictionary<string, FaceGeomHairRegionRole> roles =
            proposal.Assignments.ToImmutableDictionary(
                assignment => assignment.StructuralId,
                assignment => assignment.Role,
                StringComparer.Ordinal);
        if (actual.Regions.Any(region =>
                !roles.ContainsKey(region.StructuralId)) ||
            roles.Keys.Any(structuralId =>
                actual.Regions.All(region =>
                    !string.Equals(
                        region.StructuralId,
                        structuralId,
                        StringComparison.Ordinal))))
            throw new InvalidDataException(
                "The proposal assignments do not match source-derived structural IDs.");

        var expected = ImmutableArray.CreateBuilder<
            FaceGeomHairRegionsAuthorizedEnvelope>();
        var physicalRoles = new HashSet<FaceGeomHairRegionRole>();
        foreach (IGrouping<string, FaceGeomHairRegionsRegion> group in
                 actual.Regions
                     .GroupBy(region =>
                         region.SharedShaderGroupId)
                     .OrderBy(group =>
                         group.Min(region =>
                             region.TintByteOffset)))
        {
            FaceGeomHairRegionRole[] groupRoles = group
                .Select(region =>
                    roles[region.StructuralId])
                .Distinct()
                .ToArray();
            if (groupRoles.Length != 1)
                throw new InvalidDataException(
                    $"Source-derived shared shader group {group.Key} has conflicting roles.");
            FaceGeomHairRegionRole role = groupRoles[0];
            physicalRoles.Add(role);
            if (role == FaceGeomHairRegionRole.Preserve)
                continue;
            FaceGeomHairRegionsRegion representative =
                group.First();
            expected.Add(
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
                    FaceGeomHairRegionsSupport.ParseColorBits(
                        role == FaceGeomHairRegionRole.Primary
                            ? proposal.PrimaryColor
                            : proposal.AccentColor)));
        }
        if (!physicalRoles.Contains(
                FaceGeomHairRegionRole.Primary) ||
            !physicalRoles.Contains(
                FaceGeomHairRegionRole.Accent) ||
            !EnvelopesMatch(
                expected.ToImmutable(),
                proposal.AuthorizedEnvelopes))
            throw new InvalidDataException(
                "Authorized envelopes are not the exact source-derived non-Preserve shader groups.");
    }

    private static bool EnvelopesMatch(
        ImmutableArray<FaceGeomHairRegionsAuthorizedEnvelope> expected,
        ImmutableArray<FaceGeomHairRegionsAuthorizedEnvelope> actual)
    {
        if (expected.Length != actual.Length)
            return false;
        for (int index = 0; index < expected.Length; index++)
        {
            FaceGeomHairRegionsAuthorizedEnvelope left =
                expected[index];
            FaceGeomHairRegionsAuthorizedEnvelope right =
                actual[index];
            if (left.SharedShaderGroupId !=
                    right.SharedShaderGroupId ||
                left.Role != right.Role ||
                left.ByteOffset != right.ByteOffset ||
                left.ByteLength != right.ByteLength ||
                !left.StructuralIds.SequenceEqual(
                    right.StructuralIds,
                    StringComparer.Ordinal) ||
                !left.OldFloatBits.SequenceEqual(
                    right.OldFloatBits) ||
                !left.NewFloatBits.SequenceEqual(
                    right.NewFloatBits))
                return false;
        }
        return true;
    }
}
