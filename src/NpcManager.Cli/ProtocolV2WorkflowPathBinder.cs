using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed record ProtocolV2PhysicalFileBinding(
    WorkspacePath Path,
    string Sha256);

internal sealed class ProtocolV2WorkflowPathBinder(
    WorkspacePath workspaceRoot,
    FaceGeomHairRegionsWorkspaceBoundary boundary)
{
    private readonly List<AdmittedPathOption> admittedPaths = [];

    internal bool TryExistingFilePair(
        ParsedCommand command,
        string pathOption,
        string hashOption,
        out ProtocolV2PhysicalFileBinding? binding,
        out ImmutableArray<ProtocolDiagnostic> diagnostics)
    {
        binding = null;
        bool hasPath = command.Options.ContainsKey(pathOption);
        bool hasHash = command.Options.ContainsKey(hashOption);
        if (!hasPath || !hasHash)
        {
            string missing = !hasPath ? pathOption : hashOption;
            diagnostics =
            [
                Diagnostic(
                    ProtocolV2DiagnosticCodes.OptionRequired,
                    DiagnosticClass.Usage,
                    missing,
                    $"Options '--{pathOption}' and '--{hashOption}' must be supplied together.")
            ];
            return false;
        }

        if (!TrySemanticSha256(
                command,
                hashOption,
                out string sha256,
                out diagnostics) ||
            !TryPath(
                command,
                pathOption,
                requireDirectory: false,
                requireExisting: true,
                out WorkspacePath path,
                out diagnostics))
            return false;

        binding = new ProtocolV2PhysicalFileBinding(path, sha256);
        return true;
    }

    internal bool TryFreshFile(
        ParsedCommand command,
        string option,
        out WorkspacePath path,
        out ImmutableArray<ProtocolDiagnostic> diagnostics) =>
        TryPath(
            command,
            option,
            requireDirectory: false,
            requireExisting: false,
            out path,
            out diagnostics);

    internal bool TryFreshDirectory(
        ParsedCommand command,
        string option,
        out WorkspacePath path,
        out ImmutableArray<ProtocolDiagnostic> diagnostics) =>
        TryPath(
            command,
            option,
            requireDirectory: true,
            requireExisting: false,
            out path,
            out diagnostics);

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The approved shared binder contract exposes this as an instance operation.")]
    internal bool TrySemanticSha256(
        ParsedCommand command,
        string option,
        out string sha256,
        out ImmutableArray<ProtocolDiagnostic> diagnostics)
    {
        sha256 = string.Empty;
        if (!command.Options.TryGetValue(option, out string? value))
        {
            diagnostics =
            [
                Diagnostic(
                    ProtocolV2DiagnosticCodes.OptionRequired,
                    DiagnosticClass.Usage,
                    option,
                    $"Option '--{option}' is required.")
            ];
            return false;
        }
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')))
        {
            diagnostics =
            [
                Diagnostic(
                    ProtocolV2DiagnosticCodes.OptionSha256Value,
                    DiagnosticClass.Usage,
                    option,
                    $"Option '--{option}' requires an uppercase 64-character hexadecimal SHA-256 value.")
            ];
            return false;
        }

        sha256 = value;
        diagnostics = [];
        return true;
    }

    internal bool TryNoOverlap(
        IReadOnlyList<WorkspacePath> inputs,
        IReadOnlyList<WorkspacePath> outputs,
        out ImmutableArray<ProtocolDiagnostic> diagnostics)
    {
        foreach (WorkspacePath input in inputs)
        {
            foreach (WorkspacePath output in outputs)
            {
                if (string.Equals(
                        input.Value,
                        output.Value,
                        StringComparison.OrdinalIgnoreCase) ||
                    input.IsUnder(output) ||
                    output.IsUnder(input))
                {
                    string? inputOption = OptionFor(input, isInput: true);
                    string? outputOption = OptionFor(output, isInput: false);
                    diagnostics =
                    [
                        Diagnostic(
                            ProtocolV2DiagnosticCodes.PresetInspectionPathRefused,
                            DiagnosticClass.Security,
                            outputOption,
                            inputOption is not null && outputOption is not null
                                ? $"Options '--{inputOption}' and '--{outputOption}' must not overlap."
                                : $"Input '{input.Value}' and output '{output.Value}' must not overlap.")
                    ];
                    return false;
                }
            }
        }

        for (int leftIndex = 0; leftIndex < outputs.Count; leftIndex++)
        {
            for (int rightIndex = leftIndex + 1;
                 rightIndex < outputs.Count;
                 rightIndex++)
            {
                WorkspacePath left = outputs[leftIndex];
                WorkspacePath right = outputs[rightIndex];
                if (string.Equals(
                        left.Value,
                        right.Value,
                        StringComparison.OrdinalIgnoreCase) ||
                    left.IsUnder(right) ||
                    right.IsUnder(left))
                {
                    string? leftOption = OptionForOutputOccurrence(
                        outputs,
                        leftIndex);
                    string? rightOption = OptionForOutputOccurrence(
                        outputs,
                        rightIndex);
                    diagnostics =
                    [
                        Diagnostic(
                            ProtocolV2DiagnosticCodes.PresetInspectionPathRefused,
                            DiagnosticClass.Security,
                            rightOption,
                            leftOption is not null && rightOption is not null
                                ? $"Options '--{leftOption}' and '--{rightOption}' must not overlap."
                                : $"Outputs '{left.Value}' and '{right.Value}' must not overlap.")
                    ];
                    return false;
                }
            }
        }

        diagnostics = [];
        return true;
    }

    private bool TryPath(
        ParsedCommand command,
        string option,
        bool requireDirectory,
        bool requireExisting,
        out WorkspacePath path,
        out ImmutableArray<ProtocolDiagnostic> diagnostics)
    {
        path = workspaceRoot;
        if (!command.Options.TryGetValue(option, out string? value))
        {
            diagnostics =
            [
                Diagnostic(
                    ProtocolV2DiagnosticCodes.OptionRequired,
                    DiagnosticClass.Usage,
                    option,
                    $"Option '--{option}' is required.")
            ];
            return false;
        }

        try
        {
            path = new WorkspacePath(value);
        }
        catch (ArgumentException exception)
        {
            diagnostics =
            [
                Diagnostic(
                    ProtocolV2DiagnosticCodes.UnsafePathForm,
                    DiagnosticClass.Security,
                    option,
                    $"Option '--{option}' is not a valid absolute workspace path: {exception.Message}")
            ];
            return false;
        }

        try
        {
            if (requireExisting)
                boundary.RequireExistingFile(path, $"--{option}");
            else if (requireDirectory)
                boundary.RequireNewDirectory(path, $"--{option}");
            else
                boundary.RequireNewFile(path, $"--{option}");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            string code = PathFailureCode(option, requireExisting, exception);
            ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                code,
                out ProtocolDiagnosticSemantics semantics);
            diagnostics =
            [
                Diagnostic(
                    code,
                    semantics.Class,
                    option,
                    $"Option '--{option}' was refused: {exception.Message}")
            ];
            return false;
        }

        admittedPaths.Add(new AdmittedPathOption(
            option,
            path,
            requireExisting));
        diagnostics = [];
        return true;
    }

    private string? OptionFor(WorkspacePath path, bool isInput) =>
        admittedPaths.FirstOrDefault(item =>
            item.IsInput == isInput &&
            string.Equals(
                item.Path.Value,
                path.Value,
                StringComparison.Ordinal))?.Option ??
        admittedPaths.FirstOrDefault(item =>
            item.IsInput == isInput &&
            string.Equals(
                item.Path.Value,
                path.Value,
                StringComparison.OrdinalIgnoreCase))?.Option;

    private string? OptionForOutputOccurrence(
        IReadOnlyList<WorkspacePath> outputs,
        int index)
    {
        WorkspacePath path = outputs[index];
        int occurrence = 0;
        for (int candidateIndex = 0; candidateIndex < index; candidateIndex++)
        {
            if (string.Equals(
                    outputs[candidateIndex].Value,
                    path.Value,
                    StringComparison.OrdinalIgnoreCase))
                occurrence++;
        }

        return admittedPaths.Where(item =>
                !item.IsInput && string.Equals(
                    item.Path.Value,
                    path.Value,
                    StringComparison.OrdinalIgnoreCase))
            .Skip(occurrence)
            .Select(item => item.Option)
            .FirstOrDefault() ?? OptionFor(path, isInput: false);
    }

    private static string PathFailureCode(
        string option,
        bool requireExisting,
        Exception exception)
    {
        if (exception.Message.Contains(
                "alternate data stream",
                StringComparison.OrdinalIgnoreCase))
            return ProtocolV2DiagnosticCodes.AlternateDataStreamRefused;
        if (exception.Message.Contains(
                "reparse",
                StringComparison.OrdinalIgnoreCase))
            return ProtocolV2DiagnosticCodes.ReparsePointRefused;
        if (!requireExisting && exception.Message.Contains(
                "exist",
                StringComparison.OrdinalIgnoreCase))
            return string.Equals(
                    option,
                    "inspection-output",
                    StringComparison.Ordinal)
                ? ProtocolV2DiagnosticCodes.PresetInspectionOutputExists
                : ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused;
        return option.StartsWith("workflow-", StringComparison.Ordinal)
            ? ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused
            : ProtocolV2DiagnosticCodes.PresetInspectionPathRefused;
    }

    private static ProtocolDiagnostic Diagnostic(
        string code,
        DiagnosticClass diagnosticClass,
        string? option,
        string message) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            diagnosticClass,
            new DiagnosticRecovery(
                diagnosticClass == DiagnosticClass.Security &&
                    option?.EndsWith("output", StringComparison.Ordinal) == true
                    ? RecoveryAction.ChooseFreshOutput
                    : RecoveryAction.CorrectInput,
                option,
                null,
                "Correct the exact typed path/hash binding and retry.",
                false));

    private sealed record AdmittedPathOption(
        string Option,
        WorkspacePath Path,
        bool IsInput);
}
