using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;

namespace NpcManager.Architecture.Tests;

internal static class ProtocolDiagnosticTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void Run()
    {
        var validation = new ProtocolDiagnostic(
            "renamed-code",
            DiagnosticSeverity.Error,
            "invalid",
            DiagnosticClass.Validation,
            null);
        Require(
            ProtocolExitCodeMapper.Map([validation]) ==
            CommandExitCode.ValidationFailure,
            "classification-must-not-depend-on-code-spelling");

        ImmutableArray<ProtocolDiagnostic> mixed =
        [
            validation,
            validation with
            {
                Code = "another",
                Class = DiagnosticClass.Security
            }
        ];
        Require(
            ProtocolExitCodeMapper.Map(mixed) ==
            CommandExitCode.SecurityRefusal,
            "security-must-win-mixed-handled-failures");

        Require(
            ProtocolExitCodeMapper.Map(
            [
                validation with { Class = DiagnosticClass.Validation },
                validation with { Class = DiagnosticClass.Verification },
                validation with { Class = DiagnosticClass.Operation },
                validation with { Class = DiagnosticClass.Cancellation },
                validation with { Class = DiagnosticClass.Usage }
            ]) == CommandExitCode.UsageError,
            "usage-must-win-cancellation-operation-and-validation");
        Require(
            ProtocolExitCodeMapper.Map(
            [
                validation with { Class = DiagnosticClass.Validation },
                validation with { Class = DiagnosticClass.Verification },
                validation with { Class = DiagnosticClass.Operation },
                validation with { Class = DiagnosticClass.Cancellation }
            ]) == CommandExitCode.Cancelled,
            "cancellation-must-win-operation-and-validation");
        Require(
            ProtocolExitCodeMapper.Map(
            [
                validation with { Class = DiagnosticClass.Validation },
                validation with { Class = DiagnosticClass.Verification },
                validation with { Class = DiagnosticClass.Operation }
            ]) == CommandExitCode.GeneralFailure,
            "operation-must-win-validation");
        Require(
            ProtocolExitCodeMapper.Map(
            [
                validation with { Class = DiagnosticClass.Verification }
            ]) == CommandExitCode.ValidationFailure,
            "verification-must-map-to-validation-failure");
        Require(
            ProtocolExitCodeMapper.Map(
            [
                validation with { Severity = DiagnosticSeverity.Warning },
                validation with { Severity = DiagnosticSeverity.Info }
            ]) == CommandExitCode.Success,
            "non-errors-must-not-affect-the-exit-code");
        Require(
            ProtocolExitCodeMapper.Map([]) == CommandExitCode.Success,
            "empty-diagnostics-must-succeed");
        Require(
            ProtocolExitCodeMapper.Map(
            [
                validation with { Class = (DiagnosticClass)int.MaxValue }
            ]) == CommandExitCode.GeneralFailure,
            "unknown-diagnostic-class-must-fail-closed");

        var recovery = new DiagnosticRecovery(
            RecoveryAction.ChooseFreshOutput,
            "--output",
            "proposal",
            "destination must not exist",
            false);
        DiagnosticRecovery? attached =
            (validation with { Recovery = recovery }).Recovery;
        Require(
            attached is
            {
                Action: RecoveryAction.ChooseFreshOutput,
                Option: "--output",
                ArtifactKind: "proposal",
                Constraint: "destination must not exist",
                RetryUnchangedSafe: false
            },
            "typed-recovery-fields-must-remain-attached");

        string v1 = JsonSerializer.Serialize(
            new Diagnostic("x", DiagnosticSeverity.Error, "m"),
            JsonOptions);
        Require(
            v1 == "{\"code\":\"x\",\"severity\":\"error\",\"message\":\"m\"}",
            "v1-diagnostic-bytes-changed");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(name);
    }
}
