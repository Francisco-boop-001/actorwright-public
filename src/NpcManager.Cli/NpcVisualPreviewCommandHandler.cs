using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed class NpcVisualPreviewCommandHandler(
    IPreviewServiceFactory<INpcVisualPreviewComposer> composerFactory,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    FaceGeomHairRegionsDocumentCodec intakeCodec,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal NpcVisualPreviewCommandHandler(
        INpcVisualPreviewComposer composer,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        FaceGeomHairRegionsDocumentCodec intakeCodec,
        TextWriter output,
        TextWriter error)
        : this(
            new FixedComposerFactory(composer),
            policy,
            labRoot,
            intakeCodec,
            output,
            error)
    {
    }

    internal async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        NpcVisualPreviewCommandBindingResult bindingResult =
            NpcVisualPreviewCommandBinder.Bind(command, strict: false);
        if (!bindingResult.IsValid)
        {
            return Usage(
                command.Json,
                bindingResult.ErrorMessage ??
                "Preview inputs are invalid.");
        }
        NpcVisualPreviewCommandBinding binding = bindingResult.Binding!;

        ImmutableArray<Diagnostic> boundary =
            policy.EvaluateReadRoot(labRoot, binding.IntakePath)
                .AddRange(policy.Evaluate(labRoot, binding.OutputRoot));
        if (boundary.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            return Refused(command.Json, boundary);
        }

        ReviewedGameIntakeReadResult intakeRead =
            await ReadIntakeAsync(
                binding.IntakePath,
                cancellationToken);
        if (intakeRead.Intake is null)
        {
            return Refused(command.Json, intakeRead.Diagnostics);
        }

        var request = new NpcVisualPreviewComposeRequest(
            intakeRead.Intake,
            new SkyrimMainWorkspaceIdentity(
                binding.Plugin,
                binding.Plugin,
                binding.FormId,
                "NPC_"),
            binding.PackageOverlay,
            new NpcVisualPreviewOptions(),
            binding.OutputRoot);
        NpcVisualPreviewComposeResult result;
        try
        {
            await using PreviewServiceLease<INpcVisualPreviewComposer>
                lease = await composerFactory.CreateAsync(
                    cancellationToken);
            result = await lease.Service.ComposeAsync(
                request,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            return Usage(command.Json, exception.Message);
        }

        var response = NpcVisualPreviewResponse.From(result);
        Write(
            response,
            command.Json,
            result.Composed
                ? $"preview npc: COMPOSED — human visual and Skyrim runtime review required — {response.ContactSheetPath}"
                : "preview npc: REFUSED");
        return ExitFor(result.Diagnostics);
    }

    private sealed class FixedComposerFactory(
        INpcVisualPreviewComposer composer) :
        IPreviewServiceFactory<INpcVisualPreviewComposer>
    {
        public ValueTask<PreviewServiceLease<INpcVisualPreviewComposer>>
            CreateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new PreviewServiceLease<INpcVisualPreviewComposer>(
                    composer));
        }
    }

    private async ValueTask<ReviewedGameIntakeReadResult>
        ReadIntakeAsync(
            WorkspacePath path,
            CancellationToken cancellationToken)
    {
        try
        {
            ReviewedGameIntakeDocumentAuthority authority =
                await intakeCodec.LoadReviewedIntakeAsync(
                    path,
                    cancellationToken);
            return new(authority.Value, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            return new(
                null,
                ImmutableArray.Create(Error(
                    "npc-preview-intake-invalid",
                    $"The reviewed intake could not be read: {exception.Message}")));
        }
    }

    private CommandExitCode Refused(
        bool json,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Write(
            new NpcVisualPreviewResponse(
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                diagnostics),
            json,
            "preview npc: REFUSED");
        return ExitFor(diagnostics);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(Error(
            "usage-error",
            message));
        if (json)
        {
            error.WriteLine(JsonSerializer.Serialize(
                new ErrorResponse("usage-error", diagnostics),
                JsonOptions));
        }
        else
        {
            error.WriteLine($"ERROR usage-error: {message}");
        }
        return CommandExitCode.UsageError;
    }

    private void Write<T>(
        T response,
        bool json,
        string human)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(
                response,
                JsonOptions));
        }
        else
        {
            output.WriteLine(human);
        }
    }

    private static CommandExitCode ExitFor(
        ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            return CommandExitCode.Success;
        }
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record ReviewedGameIntakeReadResult(
        ReviewedGameIntake? Intake,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record NpcVisualPreviewResponse(
        bool Composed,
        string? SchemaVersion,
        string? SceneSchemaVersion,
        string? Route,
        string? Label,
        string? ContactSheetPath,
        string? ContactSheetSha256,
        ImmutableArray<NpcVisualPreviewViewResponse> Views,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public static NpcVisualPreviewResponse From(
            NpcVisualPreviewComposeResult result)
        {
            NpcVisualPreviewBundle? bundle = result.Bundle;
            return new(
                result.Composed,
                bundle?.SchemaVersion,
                bundle?.SceneSchemaVersion,
                bundle?.Source.Route.ToString(),
                bundle?.Label,
                bundle?.ContactSheetPath.Value,
                bundle?.ContactSheetSha256.Value,
                bundle?.Views.Select(item => new
                    NpcVisualPreviewViewResponse(
                        item.Id,
                        item.ImagePath.Value,
                        item.ImageSha256.Value,
                        item.RoleMaskPath.Value,
                        item.RoleMaskSha256.Value,
                        item.Width,
                        item.Height))
                    .ToImmutableArray() ?? [],
                result.Diagnostics);
        }
    }

    private sealed record NpcVisualPreviewViewResponse(
        string Id,
        string ImagePath,
        string ImageSha256,
        string RoleMaskPath,
        string RoleMaskSha256,
        int Width,
        int Height);

    private sealed record ErrorResponse(
        string Code,
        ImmutableArray<Diagnostic> Diagnostics);
}
