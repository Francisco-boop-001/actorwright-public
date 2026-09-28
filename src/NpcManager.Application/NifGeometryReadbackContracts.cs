using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// One independently parsed triangle-shape vertex payload. The offset and
/// length are absolute bytes in the reopened NIF, not an inferred mesh range.
/// </summary>
public sealed record NifGeometryShapeReadback(
    string Name,
    string BlockType,
    int BlockIndex,
    int VertexCount,
    ulong VertexDescriptor,
    long VertexPayloadOffset,
    int VertexPayloadLength,
    Sha256Hash VertexPayloadSha256,
    Sha256Hash TriangleTopologySha256)
{
    public int VertexStride => VertexCount == 0
        ? 0
        : checked(VertexPayloadLength / VertexCount);
}

/// <summary>Hash-bound evidence from one exact NIF byte readback.</summary>
public sealed record NifGeometryReadbackDocument(
    GameEdition Edition,
    WorkspacePath NifPath,
    Sha256Hash NifSha256,
    long ByteLength,
    int BlockCount,
    int ReachableBlockCount,
    ImmutableArray<NifGeometryShapeReadback> Shapes,
    Sha256Hash AggregateGeometrySha256,
    Sha256Hash GraphSha256)
{
    public Sha256Hash AggregateSha256 => AggregateGeometrySha256;
    public Sha256Hash GeometrySha256 => AggregateGeometrySha256;
}

/// <summary>Request to reopen one K-local Bethesda NIF independently.</summary>
public sealed record NifGeometryReadbackRequest(
    GameEdition Edition,
    WorkspacePath NifPath,
    Sha256Hash? ExpectedSha256 = null)
{
    public WorkspacePath Path => NifPath;
}

public sealed record NifGeometryReadbackResult(
    bool Accepted,
    NifGeometryReadbackDocument? Document,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Read => Accepted;
    public bool Success => Accepted;
}

/// <summary>
/// Independent Bethesda NIF geometry readback boundary. Implementations must
/// reopen bytes from the supplied path; writer-owned geometry status is not an
/// accepted source of evidence.
/// </summary>
public interface INifGeometryReadbackService
{
    ValueTask<NifGeometryReadbackResult> ReadAsync(
        NifGeometryReadbackRequest request,
        CancellationToken cancellationToken);
}
