using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcBuildService
{
    private static async ValueTask<bool> AddDirectFaceGeomEvidenceAssetAsync(
        RaceMenuDirectCharGenFaceGeomBuildResult result,
        Sha256Hash expectedCharGenSha256,
        Sha256Hash expectedCarrierSha256,
        WorkspacePath stagingRoot,
        ImmutableArray<BlankNpcTransitivePackageAsset>.Builder packageAssets,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        RaceMenuCharGenFaceGeomMergeArtifact? artifact = result.Artifact;
        RaceMenuCharGenFaceGeomMergeVerificationResult? verification =
            result.IndependentVerification;
        if (!result.Written || !result.Verified || artifact is null ||
            verification is not { Verified: true, OutputSha256: not null } ||
            artifact.Proposal.CharGenSha256 != expectedCharGenSha256 ||
            artifact.Proposal.CarrierSha256 != expectedCarrierSha256 ||
            artifact.Proposal.OutputNif.IsUnder(stagingRoot) is false ||
            artifact.OutputSha256 != verification.OutputSha256 ||
            artifact.CreationKitAuthority || artifact.RuntimeAuthority ||
            artifact.Proposal.ShapeDispositions.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error("racemenu-build-direct-facegeom-evidence-invalid",
                "Direct CharGen evidence does not retain the exact source/carrier hashes, staged output, shape closure, independent verification, and non-runtime limits."));
            return false;
        }

        byte[] bytes = SerializeDirectFaceGeomEvidence(artifact, verification);
        var evidence = new WorkspacePath(Path.Combine(
            stagingRoot.Value, "direct-chargen-facegeom-evidence.json"));
        Sha256Hash hash;
        try
        {
            hash = await WriteAndReopenNewFileAsync(
                evidence, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException)
        {
            diagnostics.Add(Error("racemenu-build-direct-facegeom-evidence-write",
                exception.Message));
            return false;
        }

        packageAssets.Add(new BlankNpcTransitivePackageAsset(
            evidence,
            hash,
            new AssetPath("NPCManager/Evidence/FaceGeom/direct-chargen-merge.json")));
        return true;
    }

    private static byte[] SerializeDirectFaceGeomEvidence(
        RaceMenuCharGenFaceGeomMergeArtifact artifact,
        RaceMenuCharGenFaceGeomMergeVerificationResult verification)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true
        });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("artifactKind", "direct-chargen-facegeom-merge-evidence");
        writer.WriteString("operation", artifact.Proposal.Operation);
        writer.WriteString(
            "qualificationProfile",
            artifact.Proposal.QualificationProfile.ToString());
        writer.WriteString("charGenSha256", artifact.Proposal.CharGenSha256.Value);
        writer.WriteString("carrierSha256", artifact.Proposal.CarrierSha256.Value);
        writer.WriteString("outputSha256", artifact.OutputSha256.Value);
        writer.WriteNumber("outputByteLength", artifact.OutputByteLength);
        writer.WriteNumber("routedShapeCount", artifact.RoutedShapeCount);
        writer.WriteNumber("changedPositionShapeCount", artifact.ChangedPositionShapeCount);
        writer.WriteNumber("expandedRadiusCount", artifact.ExpandedRadiusCount);
        writer.WriteBoolean("creationKitAuthority", artifact.CreationKitAuthority);
        writer.WriteBoolean("runtimeAuthority", artifact.RuntimeAuthority);
        writer.WriteStartArray("shapes");
        foreach (RaceMenuCharGenFaceGeomShapeDisposition shape in
                 artifact.Proposal.ShapeDispositions)
        {
            writer.WriteStartObject();
            writer.WriteString("carrierShapeName", shape.CarrierShapeName);
            writer.WriteString("route", shape.Source.Route.ToString());
            writer.WriteString("sourcePositionSha256", shape.Source.PositionSha256.Value);
            writer.WriteString("topologySha256", shape.Source.TopologySha256.Value);
            writer.WriteString("carrierPositionSha256", shape.CarrierPositionSha256.Value);
            writer.WriteBoolean("positionChanged", shape.PositionChanged);
            writer.WriteBoolean("radiusChanged", shape.RadiusChanged);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartObject("independentVerification");
        writer.WriteBoolean("verified", verification.Verified);
        writer.WriteNumber("verifiedPositionLaneCount", verification.VerifiedPositionLaneCount);
        writer.WriteNumber("verifiedRadiusCount", verification.VerifiedRadiusCount);
        writer.WriteStartArray("verifiedShapeNames");
        foreach (string name in verification.VerifiedShapeNames)
            writer.WriteStringValue(name);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static async ValueTask<Sha256Hash> WriteAndReopenNewFileAsync(
        WorkspacePath destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        string temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination.Value, overwrite: false);
            byte[] reopened = await File.ReadAllBytesAsync(
                destination.Value, cancellationToken).ConfigureAwait(false);
            if (!reopened.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException(
                    "Direct CharGen evidence changed during post-write readback.");
            return new Sha256Hash(Convert.ToHexString(SHA256.HashData(reopened)));
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
                // The caller receives write failure if the promoted file is invalid.
            }
            catch (UnauthorizedAccessException)
            {
                // The caller receives write failure if the promoted file is invalid.
            }
        }
    }
}
