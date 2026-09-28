using NpcManager.Application;

namespace NpcManager.Cli;

internal static class DiagnosticExitCodeClassifier
{
    internal static IReadOnlySet<string> RegisteredSecurityCodes =>
        ProtocolDiagnosticClassifier.RegisteredLegacySecurityCodes;

    internal static CommandExitCode KnownSecurityRefusal =>
        CommandExitCode.SecurityRefusal;

    internal static CommandExitCode Classify(
        IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return ProtocolExitCodeMapper.Map(
            diagnostics.Select(
                ProtocolDiagnosticClassifier.ClassifyLegacy));
    }

    internal static CommandExitCode ClassifyFailure(
        IEnumerable<Diagnostic> diagnostics)
    {
        CommandExitCode exitCode = Classify(diagnostics);
        return exitCode == CommandExitCode.Success
            ? CommandExitCode.ValidationFailure
            : exitCode;
    }
}
