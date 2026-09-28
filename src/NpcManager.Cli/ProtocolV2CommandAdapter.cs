using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;

namespace NpcManager.Cli;

public sealed record ProtocolCommandResult(
    ImmutableArray<ProtocolEffect> Effects,
    ImmutableArray<ProtocolDiagnostic> Diagnostics,
    ImmutableArray<ProtocolArtifact> Artifacts,
    ImmutableArray<ProtocolAuthority> Authority,
    ImmutableArray<ProtocolNextAction> NextActions,
    string? ResultSchemaId,
    JsonElement? Result)
{
    private JsonElement? result = Result?.Clone();

    public JsonElement? Result
    {
        get => result;
        init => result = value?.Clone();
    }

    [JsonIgnore]
    internal IDisposable? TerminalArtifactLease { get; init; }
}

public interface IProtocolV2CommandAdapter
{
    ImmutableArray<string> Commands { get; }

    ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command,
        string requestDigest,
        CancellationToken cancellationToken);
}
