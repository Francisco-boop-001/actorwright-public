using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed record ProtocolV2PresetInspectBinding(
    PresetParseRequest Request,
    string InputSha256,
    WorkspacePath InspectionOutput,
    WorkspacePath WorkflowInput,
    string WorkflowInputSha256,
    WorkspacePath WorkflowOutput);

internal static class ProtocolV2PresetInspectBinder
{
    internal static bool TryBind(
        ParsedCommand command,
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsWorkspaceBoundary boundary,
        out ProtocolV2PresetInspectBinding? binding,
        out ImmutableArray<ProtocolDiagnostic> diagnostics)
    {
        binding = null;
        if (!PresetCommandBinder.TryBindFormatEdition(
                command,
                out PresetFormat format,
                out GameEdition edition,
                out string errorMessage))
        {
            diagnostics = [Usage(errorMessage)];
            return false;
        }
        if (format != PresetFormat.RaceMenuJslot ||
            edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics =
            [
                Usage(
                    "Protocol-v2 preset inspect requires --format racemenu-jslot and --edition skyrimse.")
            ];
            return false;
        }

        var paths = new ProtocolV2WorkflowPathBinder(
            workspaceRoot,
            boundary);
        if (!paths.TryExistingFilePair(
                command,
                "input",
                "input-sha256",
                out ProtocolV2PhysicalFileBinding? input,
                out diagnostics))
            return false;
        if (!paths.TryFreshFile(
                command,
                "inspection-output",
                out WorkspacePath inspectionOutput,
                out diagnostics))
            return false;
        if (!paths.TryExistingFilePair(
                command,
                "workflow-bundle",
                "workflow-bundle-sha256",
                out ProtocolV2PhysicalFileBinding? workflowInput,
                out diagnostics))
            return false;
        if (!paths.TryFreshFile(
                command,
                "workflow-output",
                out WorkspacePath workflowOutput,
                out diagnostics))
            return false;
        if (!paths.TryNoOverlap(
                [input!.Path, workflowInput!.Path],
                [inspectionOutput, workflowOutput],
                out diagnostics))
            return false;

        binding = new ProtocolV2PresetInspectBinding(
            new PresetParseRequest(format, edition, input!.Path),
            input.Sha256,
            inspectionOutput,
            workflowInput!.Path,
            workflowInput.Sha256,
            workflowOutput);
        return true;
    }

    private static ProtocolDiagnostic Usage(string message) =>
        new(
            ProtocolV2DiagnosticCodes.OptionRequired,
            DiagnosticSeverity.Error,
            message,
            DiagnosticClass.Usage,
            new DiagnosticRecovery(
                RecoveryAction.CorrectInput,
                null,
                null,
                "Correct the exact preset semantics and retry.",
                false));
}
