using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;

namespace NpcManager.Cli;

public sealed record ProtocolV2Envelope(
    string ProtocolVersion,
    string SchemaVersion,
    string Command,
    ProtocolOutcome Outcome,
    int ExitCode,
    string RequestDigest,
    string? Correlation,
    ImmutableArray<ProtocolEffect> Effects,
    ImmutableArray<ProtocolDiagnostic> Diagnostics,
    ImmutableArray<ProtocolArtifact> Artifacts,
    ImmutableArray<ProtocolAuthority> Authority,
    ImmutableArray<ProtocolNextAction> NextActions,
    JsonElement? Result)
{
    private JsonElement? result = Result?.Clone();

    public JsonElement? Result
    {
        get => result;
        init => result = value?.Clone();
    }

    public static ProtocolV2Envelope Failure(
        string command,
        string requestDigest,
        CommandExitCode exitCode,
        ImmutableArray<ProtocolDiagnostic> diagnostics,
        string? correlation) =>
        new(
            "2",
            "1",
            command,
            ProtocolExitCodeMapper.MapOutcome(exitCode),
            (int)exitCode,
            requestDigest,
            correlation,
            ImmutableArray<ProtocolEffect>.Empty,
            diagnostics,
            ImmutableArray<ProtocolArtifact>.Empty,
            ImmutableArray<ProtocolAuthority>.Empty,
            ImmutableArray<ProtocolNextAction>.Empty,
            null);
}

public static class ProtocolV2EnvelopeWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public static string Serialize(ProtocolV2Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.Serialize(envelope, Options);
    }

    public static void Write(TextWriter writer, ProtocolV2Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(Serialize(envelope));
    }
}
