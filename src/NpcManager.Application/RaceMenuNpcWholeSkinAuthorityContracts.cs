using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record RaceMenuNpcWholeSkinAuthorityWriteRequest(
    SkyrimNpcWholeSkinAuthority Snapshot,
    WorkspacePath DataRoot,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder,
    WorkspacePath Destination);

public sealed record RaceMenuNpcWholeSkinAuthorityArtifact(
    RaceMenuNpcWholeSkinAuthority Authority,
    SkyrimNpcWholeSkinAuthority Snapshot);

public sealed record RaceMenuNpcWholeSkinAuthorityWriteResult(
    bool Written,
    RaceMenuNpcWholeSkinAuthorityArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuNpcWholeSkinAuthorityWriter
{
    ValueTask<RaceMenuNpcWholeSkinAuthorityWriteResult> WriteAsync(
        RaceMenuNpcWholeSkinAuthorityWriteRequest request,
        CancellationToken cancellationToken);
}

public sealed record RaceMenuNpcWholeSkinAuthorityReadRequest(
    RaceMenuNpcWholeSkinAuthority Authority,
    FormReference ExpectedRace,
    NpcSex ExpectedSex,
    FormReference? ExpectedDefaultOutfit = null)
{
    public RaceMenuNpcBodyMeshAuthority? BodyMeshAuthority { get; init; }
}

public sealed record RaceMenuNpcWholeSkinAuthorityReadResult(
    bool Accepted,
    SkyrimNpcWholeSkinAuthority? Snapshot,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuNpcWholeSkinAuthorityReader
{
    ValueTask<RaceMenuNpcWholeSkinAuthorityReadResult> ReadAsync(
        RaceMenuNpcWholeSkinAuthorityReadRequest request,
        CancellationToken cancellationToken);
}
