using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed record SkyrimNpcFinishCoreRequestDocument(
    SkyrimNpcFinishCoreRequest Value,
    WorkspacePath Path,
    long Size,
    string Sha256,
    ImmutableArray<byte> Utf8Json);

public sealed record SkyrimNpcFinishCoreProposalDocument(
    SkyrimNpcFinishCoreProposal Value,
    WorkspacePath Path,
    long Size,
    string Sha256,
    string SemanticSha256,
    ImmutableArray<byte> Utf8Json);

internal sealed class SkyrimNpcFinishCoreWorkflowDocumentLease<T>(
    T document,
    FaceGeomHairRegionsPinnedReadFile retained) : IDisposable
{
    private FaceGeomHairRegionsPinnedReadFile? retained = retained;

    internal T Document { get; } = document;

    internal void Revalidate(string expectedSha256, long expectedSize)
    {
        FaceGeomHairRegionsPinnedReadFile current = retained ??
            throw new ObjectDisposedException(GetType().Name);
        if (current.Length != expectedSize ||
            !string.Equals(
                current.ComputeSha256(),
                expectedSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The retained Finish workflow document changed after admission.");
    }

    public void Dispose() => Interlocked.Exchange(
        ref retained,
        null)?.Dispose();
}

public sealed class SkyrimNpcFinishCoreWorkflowDocumentStore
{
    private const long MaximumDocumentBytes = 4L * 1024 * 1024;
    private readonly WorkspacePath workspaceRoot;
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem;

    public SkyrimNpcFinishCoreWorkflowDocumentStore(
        WorkspacePath workspaceRoot)
    {
        this.workspaceRoot = workspaceRoot;
        fileSystem = new FaceGeomHairRegionsPinnedFileSystem(workspaceRoot);
    }

    public SkyrimNpcFinishCoreRequestDocument LoadRequest(
        WorkspacePath path,
        string expectedPhysicalSha256)
    {
        using SkyrimNpcFinishCoreWorkflowDocumentLease<
            SkyrimNpcFinishCoreRequestDocument> retained =
                LoadRequestRetained(path, expectedPhysicalSha256);
        return retained.Document;
    }

    public SkyrimNpcFinishCoreProposalDocument LoadProposalForReview(
        WorkspacePath path,
        string expectedPhysicalSha256)
    {
        using SkyrimNpcFinishCoreWorkflowDocumentLease<
            SkyrimNpcFinishCoreProposalDocument> retained =
                LoadProposalForReviewRetained(path, expectedPhysicalSha256);
        return retained.Document;
    }

    internal SkyrimNpcFinishCoreWorkflowDocumentLease<
        SkyrimNpcFinishCoreRequestDocument> LoadRequestRetained(
        WorkspacePath path,
        string expectedPhysicalSha256)
    {
        FaceGeomHairRegionsPinnedReadFile retained = OpenExact(
            path,
            expectedPhysicalSha256,
            "Finish Core request");
        try
        {
            byte[] bytes = retained.ReadExact(MaximumDocumentBytes);
            RequireExactReadback(
                bytes,
                expectedPhysicalSha256,
                "Finish Core request");
            SkyrimNpcFinishCoreRequest value =
                SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                    bytes,
                    workspaceRoot);
            byte[] canonical = SkyrimNpcFinishCoreDocumentCodec
                .SerializeRequest(value, workspaceRoot);
            if (!canonical.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException(
                    "The Finish Core request is not canonical Actorwright JSON.");
            var document = new SkyrimNpcFinishCoreRequestDocument(
                value,
                path,
                bytes.LongLength,
                expectedPhysicalSha256,
                bytes.ToImmutableArray());
            var lease = new SkyrimNpcFinishCoreWorkflowDocumentLease<
                SkyrimNpcFinishCoreRequestDocument>(document, retained);
            retained = null!;
            return lease;
        }
        finally
        {
            retained?.Dispose();
        }
    }

    internal SkyrimNpcFinishCoreWorkflowDocumentLease<
        SkyrimNpcFinishCoreProposalDocument> LoadProposalForReviewRetained(
        WorkspacePath path,
        string expectedPhysicalSha256)
    {
        FaceGeomHairRegionsPinnedReadFile retained = OpenExact(
            path,
            expectedPhysicalSha256,
            "Finish Core proposal");
        try
        {
            byte[] bytes = retained.ReadExact(MaximumDocumentBytes);
            RequireExactReadback(
                bytes,
                expectedPhysicalSha256,
                "Finish Core proposal");
            SkyrimNpcFinishCoreProposal value =
                SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
                    bytes,
                    workspaceRoot);
            byte[] canonical = SkyrimNpcFinishCoreDocumentCodec
                .SerializeProposal(value, workspaceRoot);
            if (!canonical.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException(
                    "The Finish Core proposal is not canonical Actorwright JSON.");
            if (value.Status !=
                    SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite ||
                value.Request is null ||
                value.RequestSha256 is null ||
                value.ProposalSha256 is null ||
                value.RuntimeAuthority)
                throw new InvalidDataException(
                    "GUI review requires one complete ready-for-reviewed-write Finish proposal without runtime authority.");
            string semanticSha256 = SkyrimNpcFinishCoreDocumentCodec
                .HashProposalWithoutSelf(bytes).Value;
            if (!string.Equals(
                    semanticSha256,
                    value.ProposalSha256.Value.Value,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The Finish Core proposal semantic SHA-256 is stale.");
            var document = new SkyrimNpcFinishCoreProposalDocument(
                value,
                path,
                bytes.LongLength,
                expectedPhysicalSha256,
                semanticSha256,
                bytes.ToImmutableArray());
            var lease = new SkyrimNpcFinishCoreWorkflowDocumentLease<
                SkyrimNpcFinishCoreProposalDocument>(document, retained);
            retained = null!;
            return lease;
        }
        finally
        {
            retained?.Dispose();
        }
    }

    private FaceGeomHairRegionsPinnedReadFile OpenExact(
        WorkspacePath path,
        string expectedSha256,
        string role)
    {
        if (expectedSha256.Length != 64 ||
            expectedSha256.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')))
            throw new InvalidDataException(
                $"The {role} physical SHA-256 must be uppercase hexadecimal.");
        FaceGeomHairRegionsPinnedReadFile retained =
            fileSystem.OpenRead(path, role);
        try
        {
            if (retained.Length is <= 0 or > MaximumDocumentBytes ||
                !string.Equals(
                    retained.ComputeSha256(),
                    expectedSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"The {role} physical identity differs from its admitted binding.");
            return retained;
        }
        catch
        {
            retained.Dispose();
            throw;
        }
    }

    private static void RequireExactReadback(
        ReadOnlySpan<byte> bytes,
        string expectedSha256,
        string role)
    {
        if (!string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                expectedSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The {role} bytes changed during its exact read.");
    }
}
