using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2EnvelopeTests
{
    public static void Run()
    {
        ApplicationProjectsExitCodesToWireOutcomes();
        CanonicalDigestIgnoresTransportAndOptionOrder();
        CanonicalDigestPreservesRemainingInputBytes();
        EnvelopeUsesTheExactV2WireShape();
        EnvelopeOwnsResultDocumentLifetime();
        EnvelopeOwnsResultAssignedWithInitializer();
        CanonicalDigestPreservesOptionOccurrenceSemantics();
        WriterAddsExactlyOneTrailingNewline();
        SerializationDoesNotChangeLegacyJsonDefaults();
    }

    private static void ApplicationProjectsExitCodesToWireOutcomes()
    {
        Require(
            ProtocolExitCodeMapper.MapOutcome(CommandExitCode.Success) ==
                ProtocolOutcome.Succeeded &&
            ProtocolExitCodeMapper.MapOutcome(CommandExitCode.SecurityRefusal) ==
                ProtocolOutcome.Refused &&
            ProtocolExitCodeMapper.MapOutcome(CommandExitCode.Cancelled) ==
                ProtocolOutcome.Cancelled &&
            ProtocolExitCodeMapper.MapOutcome(CommandExitCode.ValidationFailure) ==
                ProtocolOutcome.Failed,
            "Application exit-code projection changed the protocol wire outcome");
    }

    private static void CanonicalDigestIgnoresTransportAndOptionOrder()
    {
        var left = CommandLine.Parse(
            ["schema", "export", "--protocol", "2", "--json", "--command", "version", "--correlation", "a", "--correlation", "ignored", "--help"]);
        var right = CommandLine.Parse(
            ["schema", "export", "--COMMAND", "version", "--correlation", "b", "--protocol", "2", "--json"]);

        var leftDigest = ProtocolRequestDigest.Compute(left);
        var rightDigest = ProtocolRequestDigest.Compute(right);

        Require(leftDigest == rightDigest,
            "option order, option-name casing, or transport metadata changed the authority digest");
        Require(leftDigest ==
                "000E7C0FBD1C5D13D10FC18D80613313EDFAD695DEBAC448C15305FF991AFE0D",
            "canonical request bytes or exact SHA-256 representation changed");
        Require(leftDigest.Length == 64 &&
                leftDigest.All(character =>
                    character is >= '0' and <= '9' or >= 'A' and <= 'F'),
            "request digest was not exact uppercase 64-hex SHA-256");
    }

    private static void CanonicalDigestPreservesOptionOccurrenceSemantics()
    {
        var collapsed = CommandLine.Parse(
            ["schema", "export", "--command", "version"]);
        var duplicated = CommandLine.Parse(
            ["schema", "export", "--command", "version", "--command", "version"]);
        var duplicatedAgain = CommandLine.Parse(
            ["schema", "export", "--command", "version", "--command", "version", "--command", "version"]);
        Require(ProtocolRequestDigest.Compute(collapsed) !=
                ProtocolRequestDigest.Compute(duplicated) &&
                ProtocolRequestDigest.Compute(duplicated) !=
                ProtocolRequestDigest.Compute(duplicatedAgain),
            "duplicate option multiplicity collapsed to the final option value");

        var valueless = CommandLine.Parse(
            ["schema", "export", "--flag"]);
        var explicitTrue = CommandLine.Parse(
            ["schema", "export", "--flag", "true"]);
        Require(ProtocolRequestDigest.Compute(valueless) !=
                ProtocolRequestDigest.Compute(explicitTrue),
            "valueless option collided with an explicit true value");

        var ordered = CommandLine.Parse(
            ["schema", "export", "--zeta", "Z", "--flag", "--alpha", "A", "--zeta", "Z"]);
        var reordered = CommandLine.Parse(
            ["schema", "export", "--ZETA", "Z", "--zeta", "Z", "--ALPHA", "A", "--FLAG"]);
        Require(ProtocolRequestDigest.Compute(ordered) ==
                ProtocolRequestDigest.Compute(reordered),
            "unambiguous option, duplicate, or valueless ordering changed the digest");
    }

    private static void CanonicalDigestPreservesRemainingInputBytes()
    {
        var original = CommandLine.Parse(
            ["schema", "export", " Tail ", "--command", " MiXeD/value "]);
        var positionalCaseChanged = CommandLine.Parse(
            ["schema", "export", " tail ", "--command", " MiXeD/value "]);
        var optionWhitespaceChanged = CommandLine.Parse(
            ["schema", "export", " Tail ", "--command", "MiXeD/value"]);

        Require(ProtocolRequestDigest.Compute(original) !=
                ProtocolRequestDigest.Compute(positionalCaseChanged),
            "remaining positional bytes were case-normalized");
        Require(ProtocolRequestDigest.Compute(original) !=
                ProtocolRequestDigest.Compute(optionWhitespaceChanged),
            "option value bytes were trimmed or normalized");
    }

    private static void EnvelopeUsesTheExactV2WireShape()
    {
        var command = CommandLine.Parse(
            ["schema", "export", "--command", "version"]);
        var digest = ProtocolRequestDigest.Compute(command);
        var failure = ProtocolV2Envelope.Failure(
            "schema export",
            digest,
            CommandExitCode.UsageError,
            [Usage("option-unknown")],
            "request-a");

        using (var failureDocument = JsonDocument.Parse(
                   ProtocolV2EnvelopeWriter.Serialize(failure)))
        {
            var root = failureDocument.RootElement;
            Require(root.GetProperty("protocolVersion").GetString() == "2",
                "wrong protocol version");
            Require(root.GetProperty("schemaVersion").GetString() == "1",
                "wrong envelope schema version");
            Require(root.GetProperty("effects").GetArrayLength() == 0,
                "failure effects were not present and empty");
            Require(root.GetProperty("artifacts").GetArrayLength() == 0,
                "failure artifacts were not present and empty");
            Require(root.GetProperty("authority").GetArrayLength() == 0,
                "failure authority was not present and empty");
            Require(root.GetProperty("nextActions").GetArrayLength() == 0,
                "failure next actions were not present and empty");
            Require(root.GetProperty("correlation").GetString() == "request-a",
                "correlation was not preserved");
            Require(!root.TryGetProperty("result", out _),
                "null result was serialized");
        }

        using var resultDocument = JsonDocument.Parse("{\"available\":true}");
        var envelope = new ProtocolV2Envelope(
            "2",
            "1",
            "capabilities",
            ProtocolOutcome.Refused,
            (int)CommandExitCode.SecurityRefusal,
            digest,
            null,
            [ProtocolEffect.Create(
                AgentEffectKind.ReadWorkspace,
                ApplicationEffectStatus.Blocked,
                ApplicationEffectScope.Workspace)],
            [Usage("security-refusal")],
            [new ProtocolArtifact(
                "manifest",
                "application/json",
                "K:\\lab\\manifest.json",
                null,
                null,
                "capabilities",
                digest,
                ImmutableArray<string>.Empty,
                "proposed")],
            [new ProtocolAuthority(
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Required,
                "review required")],
            [new ProtocolNextAction(
                "workspace preflight",
                "admit inputs",
                [new ProtocolNextActionBinding("--input", "K:\\lab", null)],
                ImmutableArray<string>.Empty,
                true)],
            resultDocument.RootElement.Clone());

        using var document = JsonDocument.Parse(
            ProtocolV2EnvelopeWriter.Serialize(envelope));
        var serialized = document.RootElement;
        Require(serialized.GetProperty("outcome").GetString() == "refused" &&
                serialized.GetProperty("effects")[0].GetProperty("kind").GetString() ==
                "readWorkspace" &&
                serialized.GetProperty("effects")[0].GetProperty("status").GetString() ==
                "blocked" &&
                serialized.GetProperty("effects")[0].GetProperty("scope").GetString() ==
                "workspace" &&
                serialized.GetProperty("authority")[0].GetProperty("kind").GetString() ==
                "inputAdmission" &&
                serialized.GetProperty("authority")[0].GetProperty("state").GetString() ==
                "required",
            "protocol enums were not camel-case strings");
        var artifact = serialized.GetProperty("artifacts")[0];
        Require(!serialized.TryGetProperty("correlation", out _) &&
                !artifact.TryGetProperty("size", out _) &&
                !artifact.TryGetProperty("sha256", out _) &&
                !serialized.GetProperty("nextActions")[0]
                    .GetProperty("requiredBindings")[0]
                    .TryGetProperty("artifactSha256", out _),
            "nullable protocol members were not omitted");
        Require(serialized.GetProperty("result").GetProperty("available").GetBoolean(),
            "result JSON was not preserved");
    }

    private static void EnvelopeOwnsResultDocumentLifetime()
    {
        ProtocolV2Envelope envelope;
        using (var source = JsonDocument.Parse("{\"owned\":true}"))
        {
            envelope = new ProtocolV2Envelope(
                "2",
                "1",
                "capabilities",
                ProtocolOutcome.Succeeded,
                (int)CommandExitCode.Success,
                new string('C', 64),
                null,
                ImmutableArray<ProtocolEffect>.Empty,
                ImmutableArray<ProtocolDiagnostic>.Empty,
                ImmutableArray<ProtocolArtifact>.Empty,
                ImmutableArray<ProtocolAuthority>.Empty,
                ImmutableArray<ProtocolNextAction>.Empty,
                source.RootElement);
        }

        using var serialized = JsonDocument.Parse(
            ProtocolV2EnvelopeWriter.Serialize(envelope));
        Require(serialized.RootElement.GetProperty("result")
                    .GetProperty("owned").GetBoolean(),
            "envelope did not own result JSON after its source document was disposed");
    }

    private static void EnvelopeOwnsResultAssignedWithInitializer()
    {
        var envelope = ProtocolV2Envelope.Failure(
            "capabilities",
            new string('D', 64),
            CommandExitCode.GeneralFailure,
            ImmutableArray<ProtocolDiagnostic>.Empty,
            null);
        using (var source = JsonDocument.Parse("{\"assigned\":true}"))
            envelope = envelope with { Result = source.RootElement };

        using var serialized = JsonDocument.Parse(
            ProtocolV2EnvelopeWriter.Serialize(envelope));
        Require(serialized.RootElement.GetProperty("result")
                    .GetProperty("assigned").GetBoolean(),
            "envelope initializer did not own result JSON after source disposal");
    }

    private static void WriterAddsExactlyOneTrailingNewline()
    {
        var envelope = ProtocolV2Envelope.Failure(
            "capabilities",
            new string('A', 64),
            CommandExitCode.GeneralFailure,
            ImmutableArray<ProtocolDiagnostic>.Empty,
            null);
        var serialized = ProtocolV2EnvelopeWriter.Serialize(envelope);
        using var writer = new StringWriter();

        ProtocolV2EnvelopeWriter.Write(writer, envelope);

        Require(!serialized.EndsWith('\r') && !serialized.EndsWith('\n'),
            "Serialize added a trailing newline");
        Require(writer.ToString() == serialized + writer.NewLine,
            "Write did not add exactly one TextWriter newline");
    }

    private static void SerializationDoesNotChangeLegacyJsonDefaults()
    {
        var before = JsonSerializer.Serialize(
            new LegacyShape("unchanged", null));
        _ = ProtocolV2EnvelopeWriter.Serialize(ProtocolV2Envelope.Failure(
            "version",
            new string('B', 64),
            CommandExitCode.GeneralFailure,
            ImmutableArray<ProtocolDiagnostic>.Empty,
            null));
        var after = JsonSerializer.Serialize(
            new LegacyShape("unchanged", null));

        Require(before == "{\"PascalName\":\"unchanged\",\"Optional\":null}" &&
                after == before,
            "protocol v2 serialization changed protocol v1 JSON defaults");
    }

    private static ProtocolDiagnostic Usage(string code) =>
        new(
            code,
            DiagnosticSeverity.Error,
            code,
            DiagnosticClass.Usage,
            new DiagnosticRecovery(
                RecoveryAction.CorrectInput,
                null,
                null,
                "correct input",
                false));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record LegacyShape(string PascalName, string? Optional);
}
