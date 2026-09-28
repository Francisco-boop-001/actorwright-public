using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Independent Bethesda-formats verification lineage for the fixed-width
/// FaceGeom HairTint transaction. This reader does not call the
/// Infrastructure carrier codec or its fingerprint helpers.
/// </summary>
public sealed class BethesdaFaceGeomHairRegionsVerifier :
    IFaceGeomHairRegionsIndependentVerifier
{
    public FaceGeomHairRegionsVerification Verify(
        ReadOnlyMemory<byte> source,
        ReadOnlyMemory<byte> output,
        FaceGeomHairRegionsProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        byte[] outputBytes = output.ToArray();
        var observedOutput = new FaceGeomHairRegionsFile(
            proposal.Output,
            outputBytes.LongLength,
            Hash(outputBytes));
        try
        {
            byte[] sourceBytes = source.ToArray();
            ValidateByteAuthorities(
                sourceBytes,
                outputBytes,
                proposal);
            ImmutableArray<SseNifVisualMaterialDescriptor>
                sourceMaterials =
                    SseNifVisualMaterialReader.Read(sourceBytes);
            ImmutableArray<SseNifVisualMaterialDescriptor>
                outputMaterials =
                    SseNifVisualMaterialReader.Read(outputBytes);
            ValidateMaterialReadback(
                sourceMaterials,
                outputMaterials,
                proposal);
            ImmutableArray<int> changed = Diff(
                sourceBytes,
                outputBytes);
            return new FaceGeomHairRegionsVerification(
                true,
                observedOutput,
                changed,
                proposal.SourceFingerprints,
                proposal.ExpectedOutputFingerprints,
                [
                    new Diagnostic(
                        "facegeom-hair-regions-independent-pass",
                        DiagnosticSeverity.Info,
                        "Bethesda Formats reparsed the output and the whole-file diff stayed inside the promised 12-byte HairTint envelopes.")
                ]);
        }
        catch (Exception exception) when (
            exception is
                InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                OverflowException)
        {
            return new FaceGeomHairRegionsVerification(
                false,
                observedOutput,
                [],
                proposal.SourceFingerprints,
                proposal.ExpectedOutputFingerprints,
                [
                    new Diagnostic(
                        "facegeom-hair-regions-independent-fail",
                        DiagnosticSeverity.Error,
                        exception.Message)
                ]);
        }
    }

    private static void ValidateByteAuthorities(
        byte[] source,
        byte[] output,
        FaceGeomHairRegionsProposal proposal)
    {
        if (source.LongLength != proposal.Source.ByteLength ||
            Hash(source) != proposal.Source.Sha256)
            throw Invalid(
                "Independent verification rejected the source length or hash.");
        if (output.LongLength !=
            proposal.ExpectedOutput.ByteLength ||
            Hash(output) != proposal.ExpectedOutput.Sha256)
            throw Invalid(
                "Independent verification rejected the output length or hash.");
        if (source.Length != output.Length)
            throw Invalid(
                "Independent verification found a changed file length.");

        var allowed = ImmutableHashSet.CreateBuilder<int>();
        foreach (FaceGeomHairRegionsAuthorizedEnvelope envelope in
                 proposal.AuthorizedEnvelopes)
        {
            if (envelope.ByteLength != 12 ||
                envelope.Role is not (
                    FaceGeomHairRegionRole.Primary or
                    FaceGeomHairRegionRole.Accent) ||
                envelope.ByteOffset < 0 ||
                envelope.ByteOffset >
                source.LongLength - 12 ||
                envelope.OldFloatBits.Length != 3 ||
                envelope.NewFloatBits.Length != 3)
                throw Invalid(
                    "Independent verification rejected an authorized envelope.");
            for (int index = 0; index < 12; index++)
                if (!allowed.Add(
                        checked((int)envelope.ByteOffset + index)))
                    throw Invalid(
                        "Authorized HairTint envelopes overlap.");
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = checked(
                    (int)envelope.ByteOffset +
                    channel * 4);
                uint oldBits =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        source.AsSpan(offset, 4));
                uint newBits =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        output.AsSpan(offset, 4));
                if (oldBits != envelope.OldFloatBits[channel] ||
                    newBits != envelope.NewFloatBits[channel])
                    throw Invalid(
                        "Independent verification rejected promised HairTint float bits.");
            }
        }

        ImmutableArray<int> changed = Diff(source, output);
        if (!changed.SequenceEqual(
                proposal.PredictedChangedByteOffsets) ||
            changed.Any(offset => !allowed.Contains(offset)))
            throw Invalid(
                "Independent verification found an unpromised changed byte.");
    }

    private static void ValidateMaterialReadback(
        ImmutableArray<SseNifVisualMaterialDescriptor> source,
        ImmutableArray<SseNifVisualMaterialDescriptor> output,
        FaceGeomHairRegionsProposal proposal)
    {
        if (source.Length != output.Length)
            throw Invalid(
                "Bethesda Formats found changed material routing.");
        for (int index = 0; index < source.Length; index++)
        {
            SseNifVisualMaterialDescriptor left = source[index];
            SseNifVisualMaterialDescriptor right = output[index];
            if (left.Shape != right.Shape ||
                left.ShapeBlockId != right.ShapeBlockId ||
                left.ShapeBlockType != right.ShapeBlockType ||
                left.ShapeShaderReferenceByteOffset !=
                right.ShapeShaderReferenceByteOffset ||
                left.ShaderBlockId != right.ShaderBlockId ||
                left.TextureSetBlockId !=
                right.TextureSetBlockId ||
                left.TintByteOffset != right.TintByteOffset ||
                !left.ShaderOwnerShapeBlockIds.SequenceEqual(
                    right.ShaderOwnerShapeBlockIds) ||
                left.ShaderType != right.ShaderType ||
                left.ShaderFlags1 != right.ShaderFlags1 ||
                left.ShaderFlags2 != right.ShaderFlags2 ||
                left.HasAlpha != right.HasAlpha ||
                left.Alpha != right.Alpha ||
                !left.Textures.SequenceEqual(
                    right.Textures,
                    StringComparer.Ordinal))
                throw Invalid(
                    "Bethesda Formats found changed ownership, texture, or shader routing.");
        }

        ImmutableArray<SseNifVisualMaterialDescriptor> sourceHair =
            source.Where(material =>
                    material.ShaderType == 6)
                .ToImmutableArray();
        ImmutableArray<SseNifVisualMaterialDescriptor> outputHair =
            output.Where(material =>
                    material.ShaderType == 6)
                .ToImmutableArray();
        if (sourceHair.Length != proposal.Assignments.Length ||
            outputHair.Length != proposal.Assignments.Length)
            throw Invalid(
                "Bethesda Formats HairTint inventory does not match the proposal.");

        Dictionary<string, SseNifVisualMaterialDescriptor> sourceById =
            sourceHair.ToDictionary(
                StructuralId,
                StringComparer.Ordinal);
        Dictionary<string, SseNifVisualMaterialDescriptor> outputById =
            outputHair.ToDictionary(
                StructuralId,
                StringComparer.Ordinal);
        if (proposal.Assignments.Any(assignment =>
                !sourceById.ContainsKey(
                    assignment.StructuralId)) ||
            sourceById.Keys.Any(structuralId =>
                proposal.Assignments.All(assignment =>
                    !string.Equals(
                        assignment.StructuralId,
                        structuralId,
                        StringComparison.Ordinal))))
            throw Invalid(
                "Bethesda Formats structural HairTint IDs do not match the proposal.");

        if (proposal.Assignments
                .Select(assignment => assignment.StructuralId)
                .Distinct(StringComparer.Ordinal)
                .Count() != proposal.Assignments.Length)
            throw Invalid(
                "The proposal contains duplicate structural assignments.");
        Dictionary<string, FaceGeomHairRegionRole> assignmentRoles =
            proposal.Assignments.ToDictionary(
                assignment => assignment.StructuralId,
                assignment => assignment.Role,
                StringComparer.Ordinal);
        ILookup<string, FaceGeomHairRegionsAuthorizedEnvelope>
            envelopesByGroup =
                proposal.AuthorizedEnvelopes.ToLookup(
                    envelope =>
                        envelope.SharedShaderGroupId,
                    StringComparer.Ordinal);
        var independentlyDerivedGroups =
            sourceHair.GroupBy(
                    material => material.ShaderBlockId)
                .OrderBy(group => group.Key)
                .ToArray();
        foreach (IGrouping<int, SseNifVisualMaterialDescriptor>
                 group in independentlyDerivedGroups)
        {
            string sharedId = $"shader:{group.Key}";
            string[] independentlyOwnedIds = group
                .Select(StructuralId)
                .Order(StringComparer.Ordinal)
                .ToArray();
            FaceGeomHairRegionRole[] roles =
                independentlyOwnedIds.Select(structuralId =>
                {
                    if (!assignmentRoles.TryGetValue(
                            structuralId,
                            out FaceGeomHairRegionRole role))
                        throw Invalid(
                            "An independently derived HairTint owner has no assignment.");
                    return role;
                }).Distinct().ToArray();
            if (roles.Length != 1)
                throw Invalid(
                    "Assignments disagree inside one physical shader group.");
            FaceGeomHairRegionRole role = roles[0];
            FaceGeomHairRegionsAuthorizedEnvelope[] envelopes =
                envelopesByGroup[sharedId].ToArray();
            if (role == FaceGeomHairRegionRole.Preserve)
            {
                if (envelopes.Length != 0)
                    throw Invalid(
                        "A Preserve physical shader group has an authorized envelope.");
                continue;
            }
            if (role is not (
                    FaceGeomHairRegionRole.Primary or
                    FaceGeomHairRegionRole.Accent) ||
                envelopes.Length != 1)
                throw Invalid(
                    "Every selected physical shader group requires exactly one envelope.");

            FaceGeomHairRegionsAuthorizedEnvelope envelope =
                envelopes[0];
            if (envelope.Role != role)
                throw Invalid(
                    "An envelope role disagrees with its assignments.");
            if (!independentlyOwnedIds.SequenceEqual(
                    envelope.StructuralIds
                        .Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
                throw Invalid(
                    "An envelope does not include every independently derived shader owner.");
            SseNifVisualMaterialDescriptor representative =
                group.First();
            if (representative.TintByteOffset !=
                    envelope.ByteOffset ||
                !representative.TintFloatBits.SequenceEqual(
                    envelope.OldFloatBits) ||
                group.Any(material =>
                    material.TintByteOffset !=
                        representative.TintByteOffset ||
                    !material.ShaderOwnerShapeBlockIds
                        .Order()
                        .SequenceEqual(
                            group.Select(owner =>
                                    owner.ShapeBlockId)
                                .Order())))
                throw Invalid(
                    "An envelope offset, bits, or shader ownership disagrees with Bethesda Formats.");
            ImmutableArray<uint> targetBits =
                DeriveColorBits(
                    role == FaceGeomHairRegionRole.Primary
                        ? proposal.PrimaryColor
                        : proposal.AccentColor);
            if (!envelope.NewFloatBits.SequenceEqual(
                    targetBits))
                throw Invalid(
                    "Envelope target bits do not derive from the canonical selected color.");
            if (group.Any(material =>
                    !outputById[StructuralId(material)]
                        .TintFloatBits.SequenceEqual(targetBits)))
                throw Invalid(
                    "Selected output bits do not equal the independently derived target.");
        }
        if (proposal.AuthorizedEnvelopes.Any(envelope =>
                independentlyDerivedGroups.All(group =>
                    !string.Equals(
                        envelope.SharedShaderGroupId,
                        $"shader:{group.Key}",
                        StringComparison.Ordinal))))
            throw Invalid(
                "An envelope names no independently derived physical shader group.");

        foreach (FaceGeomHairRegionAssignment assignment in
                 proposal.Assignments)
        {
            SseNifVisualMaterialDescriptor left =
                sourceById[assignment.StructuralId];
            SseNifVisualMaterialDescriptor right =
                outputById[assignment.StructuralId];
            FaceGeomHairRegionRole role =
                assignment.Role;
            string? expected = role !=
                FaceGeomHairRegionRole.Preserve
                ? role == FaceGeomHairRegionRole.Primary
                    ? proposal.PrimaryColor
                    : proposal.AccentColor
                : left.TintHex;
            if (!string.Equals(
                    right.TintHex,
                    expected,
                    StringComparison.Ordinal))
                throw Invalid(
                    "Bethesda Formats found incorrect selected or preserved HairTint semantics.");
            if (role == FaceGeomHairRegionRole.Preserve &&
                !left.TintFloatBits.SequenceEqual(
                    right.TintFloatBits))
                throw Invalid(
                    "Bethesda Formats found changed bits on a Preserve shape.");
        }
    }

    private static string StructuralId(
        SseNifVisualMaterialDescriptor material) =>
        $"shape:{material.ShapeBlockId}:shader:{material.ShaderBlockId}";

    private static StructuralParts ParseStructuralId(
        string structuralId)
    {
        string[] parts = structuralId.Split(':');
        if (parts.Length != 4 ||
            parts[0] != "shape" ||
            parts[2] != "shader" ||
            !int.TryParse(parts[1], out int shapeId) ||
            !int.TryParse(parts[3], out int shaderId) ||
            shapeId < 0 ||
            shaderId < 0)
            throw Invalid(
                "The proposal contains an invalid structural shape ID.");
        return new StructuralParts(shapeId, shaderId);
    }

    private static ImmutableArray<int> Diff(
        byte[] source,
        byte[] output) =>
        Enumerable.Range(0, source.Length)
            .Where(index => source[index] != output[index])
            .ToImmutableArray();

    private static ImmutableArray<uint> DeriveColorBits(
        string color)
    {
        if (color.Length != 7 ||
            color[0] != '#' ||
            color.Skip(1).Any(character =>
                !((character >= '0' &&
                   character <= '9') ||
                  (character >= 'A' &&
                   character <= 'F'))))
            throw Invalid(
                "The selected color is not canonical #RRGGBB.");
        var bits = ImmutableArray.CreateBuilder<uint>(3);
        for (int index = 1; index < 7; index += 2)
        {
            byte channel = Convert.ToByte(
                color.Substring(index, 2),
                16);
            bits.Add(
                BitConverter.SingleToUInt32Bits(
                    channel / 255F));
        }
        return bits.MoveToImmutable();
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static InvalidDataException Invalid(string message) =>
        new(message);

    private readonly record struct StructuralParts(
        int ShapeId,
        int ShaderId);
}
