using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Whether a FaceGeom request evaluates TRI morphs or transports finished bytes.</summary>
public enum FaceGeomBinaryOperation
{
    Bake,
    Transport
}

/// <summary>Which already-finished Skyrim SE carrier transport is requested.</summary>
public enum FaceGeomTransportProfile
{
    CompleteCarrier,
    GeometryIntoCarrier
}

/// <summary>One explicitly selected finite TRI morph value for a sandbox FaceGeom bake; extended values are not clamped.</summary>
public sealed record FaceGeomBinaryMorph(string Name, float Value);

/// <summary>One source dependency copied under the K-local FaceGeom asset root.</summary>
public sealed record FaceGeomBinarySource(string Path, string Sha256);

/// <summary>
/// A hash-bound NIF produced by importing one head mesh, applying TRI morphs, and
/// baking the evaluated vertices. It is not runtime or provider authority.
/// </summary>
public sealed record FaceGeomBinaryBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string OutputPath,
    string OutputSha256,
    long ByteLength,
    int VertexCount,
    string InputSourceSha256,
    ImmutableArray<FaceGeomBinarySource> TriFiles,
    ImmutableArray<FaceGeomBinaryMorph> Morphs,
    string BaseVertexSha256,
    string BakedVertexSha256,
    string ImportMode,
    bool RuntimeAuthority,
    string? CarrierVertexSha256 = null);

public sealed record FaceGeomBinaryBuildRequest(
    GameEdition Edition,
    WorkspacePath AssetRoot,
    WorkspacePath SourceNif,
    WorkspacePath OutputPath,
    ImmutableArray<FaceGeomBinaryMorph> Morphs,
    FaceGeomBinaryOperation Operation = FaceGeomBinaryOperation.Bake,
    FaceGeomTransportProfile? TransportProfile = null,
    Sha256Hash? SourceSha256 = null,
    WorkspacePath? CarrierPath = null,
    Sha256Hash? CarrierSha256 = null,
    string? ShapeName = null)
{
    public Sha256Hash? SourceNifSha256 => SourceSha256;
    public WorkspacePath? Carrier => CarrierPath;
    public Sha256Hash? CarrierNifSha256 => CarrierSha256;
}

public sealed record FaceGeomBinaryBuildResult(
    bool Written,
    FaceGeomBinaryBuildArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGeomBinaryBuildService
{
    ValueTask<FaceGeomBinaryBuildResult> BuildAsync(
        FaceGeomBinaryBuildRequest request, CancellationToken cancellationToken);
}
